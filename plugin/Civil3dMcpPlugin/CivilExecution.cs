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
    CancellationToken commandContextStartCancellationToken = default)
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

      try
      {
        progress?.SetStage(OperationStage.ResolvingDrawing);
        var doc = App.DocumentManager.MdiActiveDocument
          ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
        var civilDoc = CivilApplication.ActiveDocument
          ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active Civil 3D document is available.");
        var database = doc.Database;

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

  /// <summary>Execute a read-only operation (no commit).</summary>
  public static Task<T> ReadAsync<T>(
    Func<Document, CivilDocument, Database, Transaction, T> action)
    => ExecuteAsync(action, false);

  /// <summary>Execute a write operation (commits the transaction).</summary>
  public static Task<T> WriteAsync<T>(
    Func<Document, CivilDocument, Database, Transaction, T> action)
    => ExecuteAsync(action, true);
}
