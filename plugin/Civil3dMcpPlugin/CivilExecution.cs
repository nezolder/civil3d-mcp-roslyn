using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using System.Diagnostics;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

/// <summary>
/// Helper for executing Civil 3D API operations safely on the main thread.
/// The Civil 3D API is single-threaded — all operations must be marshaled to
/// the main AutoCAD thread through a correlated modal command.
/// </summary>
public static class CivilExecution
{
  // Explicit legacy comparison route. Never switch or replay an in-flight request.
  private static readonly bool NativeBackendConfigured = string.Equals(
    Environment.GetEnvironmentVariable("CIVIL3D_MCP_EXECUTION_BACKEND"),
    "native", StringComparison.OrdinalIgnoreCase);
  internal static bool? UseNativeBackendOverrideForTests { get; set; }
  internal static bool UseNativeBackend => UseNativeBackendOverrideForTests ?? NativeBackendConfigured;
  /// <summary>
  /// Execute an operation within a proper document lock and transaction.
  /// If <paramref name="write"/> is true, the transaction is committed.
  /// </summary>
  public static Task<T> ExecuteAsync<T>(
    Func<Document, CivilDocument, Database, Transaction, T> action,
    bool write)
    => ExecuteAsync(action, write, null, null);

  internal static async Task<T> ExecuteAsync<T>(
    Func<Document, CivilDocument, Database, Transaction, T> action,
    bool write,
    ExpectedDrawing? expectedDrawing,
    InternalBenchmarkMeasurement? benchmarkMeasurement,
    bool saveDrawing = false,
    OperationProgress? progress = null,
    CancellationToken commandContextStartCancellationToken = default,
    ExecutionRecord? record = null)
  {
    T? result = default;
    Exception? capturedException = null;
    var operationCommitted = false;
    var commandContextRequestedAt = benchmarkMeasurement == null
      ? (long?)null
      : Stopwatch.GetTimestamp();

    progress?.ConfigureExecution(write, saveDrawing);
    progress?.SetStage(OperationStage.WaitingForCommandContext);
    void ExecuteBody()
    {
      progress?.RecordCallbackEntered();
      var executionStartedAt = benchmarkMeasurement == null
        ? (long?)null
        : Stopwatch.GetTimestamp();
      if (commandContextRequestedAt is long requestedAt)
      {
        benchmarkMeasurement!.RecordCommandContextWait(
          Stopwatch.GetElapsedTime(requestedAt, executionStartedAt!.Value)
        );
      }

      // Only a write gets a receipt and a journal line.
      var writeRecord = write ? record : null;
      string? namedDrawingPath = null;
      try
      {
        progress?.SetStage(OperationStage.ResolvingDrawing);
        var doc = App.DocumentManager.MdiActiveDocument
          ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
        var civilDoc = CivilApplication.ActiveDocument
          ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active Civil 3D document is available.");
        var database = doc.Database;
        writeRecord?.RecordDrawing(database.Filename, database.FingerprintGuid.ToString());

        result = DrawingGuard.ValidateThenRun(
          expectedDrawing,
          database.Filename,
          database.FingerprintGuid,
          () =>
          {
            if (saveDrawing)
            {
              var drawingHasFileName = Convert.ToInt16(
                App.GetSystemVariable("DWGTITLED")
              ) != 0;
              if (!drawingHasFileName || string.IsNullOrWhiteSpace(database.Filename))
              {
                throw new JsonRpcDispatchException(
                  "CIVIL3D.SAVE_PATH_REQUIRED",
                  "The active drawing has not been named yet. Save it once in Civil 3D before using saveDrawing."
                );
              }
              // Checked before the script runs: otherwise the write would be
              // committed in memory and only the save at the end would fail.
              if (doc.IsReadOnly)
              {
                throw new JsonRpcDispatchException(
                  "CIVIL3D.DRAWING_READ_ONLY",
                  "The active drawing is open read-only, so saveDrawing cannot write its file. Nothing was run. " +
                    "Reopen the drawing for writing, or run without saveDrawing and save a copy in Civil 3D."
                );
              }
            }

            if (writeRecord != null)
            {
              namedDrawingPath = ApplyWriteSafety(writeRecord, doc, database.Filename);
            }

            progress?.SetStage(OperationStage.AcquiringDocumentLock);
            using (var documentLock = doc.LockDocument())
            {
              try
              {
                progress?.SetStage(OperationStage.StartingTransaction);
                using (var transaction = database.TransactionManager.StartTransaction())
                {
                  try
                  {
                    var actionResult = action(doc, civilDoc, database, transaction);

                    if (write)
                    {
                      progress?.SetStage(OperationStage.CommittingTransaction);
                      progress?.RecordCommit(null);
                      try
                      {
                        transaction.Commit();
                        operationCommitted = true;
                        progress?.RecordCommit(true);
                      }
                      catch
                      {
                        progress?.RecordCommit(false);
                        throw;
                      }
                    }

                    return actionResult;
                  }
                  finally
                  {
                    progress?.SetStage(OperationStage.DisposingTransaction);
                  }
                }
              }
              finally
              {
                progress?.SetStage(OperationStage.DisposingDocumentLock);
              }
            }
          }
        );

        if (saveDrawing)
        {
          try
          {
            progress?.SetStage(OperationStage.SavingDrawing);
            progress?.RecordSave(null);
            // Saving inside the Roslyn transaction can fail with eFilerError.
            // Run it only after both the transaction and document lock are disposed.
            database.SaveAs(
              database.Filename,
              true,
              database.OriginalFileVersion,
              database.SecurityParameters
            );
            progress?.RecordSave(true);
            if (writeRecord != null) writeRecord.Saved = true;
          }
          catch (Exception ex)
          {
            progress?.RecordSave(false);
            throw new JsonRpcDispatchException(
              "CIVIL3D.SAVE_FAILED",
              $"Drawing changes were committed in memory, but saving the active drawing failed: {ex.Message}",
              operationCommitted: true
            );
          }
        }
      }
      catch (Exception ex)
      {
        capturedException = ex;
      }
      finally
      {
        if (writeRecord != null)
        {
          writeRecord.Committed = operationCommitted;
          if (writeRecord.ReachedDrawing)
          {
            writeRecord.DbmodAfter = TryReadDbmod();
            writeRecord.RecordFile(namedDrawingPath);
          }
        }
        // Still on the main thread: keep the health reply's drawing identity current.
        ActiveDrawingTracker.Refresh();
        if (executionStartedAt is long startedAt)
        {
          benchmarkMeasurement!.RecordExecution(Stopwatch.GetElapsedTime(startedAt));
        }
        // This marker does not release the gate. The host command must still
        // finish, even when the body has disposed every drawing resource.
        progress?.SetStage(OperationStage.WaitingForCommandContextCompletion);
        progress?.RecordCallbackExited(capturedException != null);
      }

    }

    if (UseNativeBackend)
    {
      await CommandContextAdmission.ExecuteAsync(App.DocumentManager, async _ =>
      {
        ExecuteBody();
        await Task.CompletedTask;
      }, null, commandContextStartCancellationToken, progress);
    }
    else
    {
      await ModalCommandAdmission.ExecuteAsync(
        ExecuteBody, commandContextStartCancellationToken, () => operationCommitted);
    }

    progress?.SetStage(OperationStage.ReturningResult);

    if (capturedException != null)
    {
      throw capturedException;
    }

    return result!;
  }

  /// <summary>
  /// Runs before the document lock and the script: notes the unsaved-changes
  /// state, refuses a drawing that is not clean when that was asked for, makes
  /// the backup copy, and shows the caller's description on the command line.
  /// Returns the drawing's file path when the drawing has been named.
  /// </summary>
  private static string? ApplyWriteSafety(ExecutionRecord record, Document doc, string? databaseFilename)
  {
    var dbmod = TryReadDbmod();
    record.DbmodBefore = dbmod;

    string? namedDrawingPath = null;
    try
    {
      if (Convert.ToInt16(App.GetSystemVariable("DWGTITLED")) != 0
        && !string.IsNullOrWhiteSpace(databaseFilename))
      {
        namedDrawingPath = databaseFilename;
      }
    }
    catch (Exception)
    {
      // Unknown counts as not named: no file facts in the receipt, no backup.
    }

    if (record.RequireClean && dbmod != 0)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.DRAWING_NOT_CLEAN",
        dbmod == null
          ? "It could not be checked whether the active drawing has unsaved changes, so nothing was run."
          : $"The active drawing has unsaved changes (DBMOD={dbmod.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}), so nothing was run. " +
            (record.RequireCleanFromBackup
              ? "The backup is a copy of the saved file and would not contain them. Save the drawing in Civil 3D first, " +
                "or repeat the call with requireClean: false to accept a backup of the last saved state."
              : "Save the drawing in Civil 3D first, or repeat the call without requireClean if the unsaved state is intended.")
      );
    }

    if (record.Backup)
    {
      if (namedDrawingPath == null)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.SAVE_PATH_REQUIRED",
          "The active drawing has not been named yet, so there is no file to back up. Nothing was run. " +
            "Save it once in Civil 3D before using backup."
        );
      }
      record.BackupPath = DrawingBackup.Create(namedDrawingPath, record.BackupDirectory, DateTime.Now);
    }

    EchoDescription(doc, record.Description);
    return namedDrawingPath;
  }

  /// <summary>DBMOD is 0 when the drawing has no unsaved changes. Null when it cannot be read.</summary>
  private static int? TryReadDbmod()
  {
    try
    {
      return Convert.ToInt32(App.GetSystemVariable("DBMOD"), System.Globalization.CultureInfo.InvariantCulture);
    }
    catch (Exception)
    {
      return null;
    }
  }

  /// <summary>
  /// Lets the person at the computer see what the assistant is about to do.
  /// The text is the caller's, so anything the command line could treat as a
  /// format instruction is replaced first.
  /// </summary>
  private static void EchoDescription(Document doc, string? description)
  {
    var text = ExecutionRecord.ToSingleLine(description, 200);
    if (text == null) return;
    try
    {
      text = text.Replace("%", "pct").Replace('{', '(').Replace('}', ')');
      doc.Editor.WriteMessage("\n[Civil 3D MCP] " + text + "\n");
    }
    catch (Exception)
    {
      // A message that cannot be shown must not stop the write.
    }
  }

  /// <summary>Execute a read-only operation (no commit).</summary>
  public static Task<T> ReadAsync<T>(
    Func<Document, CivilDocument, Database, Transaction, T> action)
    => ExecuteAsync(action, false);

  /// <summary>Execute a write operation (commits the transaction).</summary>
  public static Task<T> WriteAsync<T>(
    Func<Document, CivilDocument, Database, Transaction, T> action)
    => ExecuteAsync(action, true);
}
