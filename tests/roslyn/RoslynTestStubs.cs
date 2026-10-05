// Minimal stand-ins so the real RoslynExecutor compiles and runs scripts
// without Autodesk assemblies. Every namespace the executor imports must exist.

namespace Autodesk.AutoCAD.ApplicationServices { public class ApplicationServicesStub { } }
namespace Autodesk.AutoCAD.EditorInput { public class EditorInputStub { } }
namespace Autodesk.AutoCAD.Geometry { public class GeometryStub { } }
namespace Autodesk.AutoCAD.Runtime { public class RuntimeStub { } }
namespace Autodesk.Civil { public class CivilStub { } }
namespace Autodesk.Civil.ApplicationServices { public class CivilApplicationServicesStub { } }
namespace Autodesk.Civil.Settings { public class SettingsStub { } }

namespace Autodesk.AutoCAD.DatabaseServices
{
  public class Entity
  {
    public string Name { get; set; } = "entity";
    public string Layer { get; set; } = "0";
  }
}

namespace Autodesk.Civil.DatabaseServices
{
  public class Alignment : Autodesk.AutoCAD.DatabaseServices.Entity
  {
    public double Length => 12.5;
    public double StartingStation => 0;
  }
}

namespace Autodesk.Civil.DatabaseServices.Styles
{
  public class SurfaceStyle { }
}

namespace Civil3DMcpPlugin
{
  /// <summary>Script globals; the real one wraps the active Civil document.</summary>
  public class ScriptContext
  {
    public int Value { get; set; } = 41;
  }

  /// <summary>Same shape as the plugin's exception in PluginRuntime.cs.</summary>
  public sealed class JsonRpcDispatchException : Exception
  {
    public JsonRpcDispatchException(string code, string message, bool operationCommitted = false)
      : base(message)
    {
      Code = code;
      OperationCommitted = operationCommitted;
    }

    public string Code { get; }
    public bool OperationCommitted { get; }
  }
}
