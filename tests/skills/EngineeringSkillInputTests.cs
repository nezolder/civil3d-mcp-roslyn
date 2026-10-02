using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Only validation/statistics prefixes execute. Native Civil members are not referenced by this assembly.
internal static class EngineeringSkillInputTests
{
    private sealed record Probe(string Name, string File, bool Expected,
        Dictionary<string, string> Values, string? Tail = null, Action<object>? Check = null);

    internal static void Run(string root, IEnumerable<MetadataReference> references)
    {
        var probes = new List<Probe>();
        var pvis = "profiles/create_design_profile_from_pvis.skill.md";
        var profile = new Dictionary<string, string> {
            ["alignmentHandle"] = "\"AB12\"", ["profileName"] = "\"Design-01\"",
            ["profileStyleName"] = "\"Design style\"", ["profileLabelSetName"] = "\"Design labels\"",
            ["pviData"] = "new (double station, double elevation, double curveLength)[] { (0,100,0), (100,102,40), (200,101,0) }"
        };
        Add(pvis, "unconfigured profile refuses authoring", false, new());
        Add(pvis, "symmetric curve input reaches host boundary", true, profile);
        Add(pvis, "two-point straight grade accepted", true, Change(profile, "pviData", "new (double station, double elevation, double curveLength)[] {(10,100,0),(20,101,0)}"));
        Add(pvis, "descending stations refused", false, Change(profile, "pviData", "new (double station, double elevation, double curveLength)[] {(20,100,0),(10,101,0)}"));
        Add(pvis, "duplicate stations refused", false, Change(profile, "pviData", "new (double station, double elevation, double curveLength)[] {(10,100,0),(10,101,0)}"));
        Add(pvis, "nonfinite elevations refused", false, Change(profile, "pviData", "new (double station, double elevation, double curveLength)[] {(0,100,0),(100,double.NaN,0)}"));
        Add(pvis, "endpoint curve refused", false, Change(profile, "pviData", "new (double station, double elevation, double curveLength)[] {(0,100,10),(100,101,0)}"));
        Add(pvis, "overlapping curves refused", false, Change(profile, "pviData", "new (double station, double elevation, double curveLength)[] {(0,100,0),(100,102,150),(200,100,150),(300,102,0)}"));
        Add(pvis, "collinear curved PVI refused", false, Change(profile, "pviData", "new (double station, double elevation, double curveLength)[] {(0,100,0),(100,101,40),(200,102,0)}"));
        Add(pvis, "unconfigured style refused", false, Change(profile, "profileStyleName", "\"PROFILE_STYLE_NAME\""));
        Add(pvis, "over-limit PVI list refused", false, Change(profile, "pviData", "Enumerable.Range(0,201).Select(i=>(station:(double)i,elevation:100.0,curveLength:0.0)).ToArray()"));

        var sections = "sections/create_section_views.skill.md";
        var section = new Dictionary<string, string> {
            ["groupHandle"]="\"AB12\"", ["startStation"]="0.0", ["endStation"]="100.0",
            ["viewNamePrefix"]="\"Cross section\"", ["styleName"]="\"Road section\"", ["bandSetStyleName"]="\"Road bands\""
        };
        Add(sections,"unconfigured views refuse authoring",false,new());
        Add(sections,"configured section selection accepted",true,section);
        Add(sections,"single-station selection accepted",true,Change(section,"endStation","0.0"));
        Add(sections,"reversed range refused",false,Change(section,"endStation","-1.0"));
        Add(sections,"nonfinite placement refused",false,Change(section,"originX","double.PositiveInfinity"));
        Add(sections,"negative clearance refused",false,Change(section,"gapY","-1.0"));
        Add(sections,"over-limit view batch refused",false,Change(section,"viewLimit","51"));
        Add(sections,"zero columns refused",false,Change(section,"columns","0"));
        Add(sections,"placeholder band style refused",false,Change(section,"bandSetStyleName","\"SECTION_VIEW_BAND_SET_STYLE_NAME\""));

        var comparison="surfaces/compare_surface_elevations.skill.md";
        var surface = new Dictionary<string,string> {
            ["surfaceAHandle"]="\"AB12\"", ["surfaceBHandle"]="\"CD34\"",
            ["samplePoints"]="new (double x,double y)[] {(0,0),(1,1)}", ["tolerance"]="1.0"
        };
        Add(comparison,"unconfigured comparison refused",false,new());
        Add(comparison,"configured comparison accepted",true,surface);
        Add(comparison,"same surface handle refused",false,Change(surface,"surfaceBHandle","\"ab12\""));
        Add(comparison,"duplicate XY samples refused",false,Change(surface,"samplePoints","new (double x,double y)[] {(0,0),(0,0)}"));
        Add(comparison,"nonfinite XY refused",false,Change(surface,"samplePoints","new (double x,double y)[] {(0,double.NaN)}"));
        Add(comparison,"negative tolerance refused",false,Change(surface,"tolerance","-0.1"));
        Add(comparison,"over-limit point set refused",false,Change(surface,"samplePoints","Enumerable.Range(0,2001).Select(i=>(x:(double)i,y:0.0)).ToArray()"));
        Add(comparison,"over-limit details refused",false,Change(surface,"detailLimit","201"));
        Add(comparison,"zero tolerance accepted",true,Change(surface,"tolerance","0.0"));
        probes.Add(new("signed statistics match hand-calculated values",comparison,true,surface,
            "return new { success=true, statistics=summarize(new double[] {-1,0,1,2}) };", result=> {
                var s=Property(result,"statistics")!;
                Near(s,"mean",0.5); Near(s,"rmse",Math.Sqrt(1.5)); Near(s,"mae",1);
                Near(s,"standardDeviation",Math.Sqrt(1.25)); Near(s,"median",0.5); Near(s,"p95Absolute",1.85);
                Equal(s,"withinTolerance",3); Equal(s,"outsideTolerance",1);
            }));
        probes.Add(new("missing coverage cannot masquerade as zero error",comparison,true,surface,
            "return new { success=true, statistics=summarize(Array.Empty<double>()) };", result=> {
                if(Property(result,"statistics") is not null) throw new InvalidOperationException("Empty differences require null statistics.");
            }));
        probes.Add(new("identical sampled elevations produce zero statistics",comparison,true,surface,
            "return new { success=true, statistics=summarize(new double[] {0,0,0}) };", result=> {
                var s=Property(result,"statistics")!; Near(s,"rmse",0); Near(s,"standardDeviation",0); Equal(s,"withinTolerance",3);
            }));
        probes.Add(new("large finite differences avoid square overflow",comparison,true,surface,
            "return new { success=true, statistics=summarize(new double[] {1e200,1e200}) };", result=> {
                var s=Property(result,"statistics")!; Near(s,"rmse",1e200,1e-12); Near(s,"mean",1e200,1e-12); Near(s,"standardDeviation",0);
            }));

        var quantities="sections/material_quantity_report.skill.md";
        var quantity=new Dictionary<string,string> {
            ["groupHandle"]="\"AB12\"", ["materialListGuid"]="\"11111111-1111-1111-1111-111111111111\"",
            ["startStation"]="0.0",["endStation"]="100.0"
        };
        Add(quantities,"unconfigured quantity selection refused",false,new());
        Add(quantities,"configured quantity selection accepted",true,quantity);
        Add(quantities,"empty GUID refused",false,Change(quantity,"materialListGuid","\"00000000-0000-0000-0000-000000000000\""));
        Add(quantities,"malformed GUID refused",false,Change(quantity,"materialListGuid","\"first list\""));
        Add(quantities,"zero quantity row limit refused",false,Change(quantity,"limit","0"));
        Add(quantities,"over-limit quantity report refused",false,Change(quantity,"limit","1001"));
        Add(quantities,"reversed quantity range refused",false,Change(quantity,"startStation","101.0"));

        var target="corridors/corridor_target_audit.skill.md";
        var targetValues=new Dictionary<string,string>{["corridorHandle"]="\"AB12\""};
        Add(target,"placeholder corridor refused",false,new());
        Add(target,"configured corridor audit accepted",true,targetValues);
        Add(target,"zero corridor handle refused",false,Change(targetValues,"corridorHandle","\"0\""));
        Add(target,"signed-overflow corridor handle refused",false,Change(targetValues,"corridorHandle","\"FFFFFFFFFFFFFFFF\""));
        Add(target,"zero target limit refused",false,Change(targetValues,"targetLimit","0"));
        Add(target,"over-limit mapped objects refused",false,Change(targetValues,"targetObjectLimit","26"));

        var dref="references/data_reference_audit.skill.md";
        Add(dref,"default bounded reference audit accepted",true,new());
        Add(dref,"zero reference detail limit refused",false,new(){["limit"]="0"});
        Add(dref,"over-limit reference details refused",false,new(){["limit"]="101"});
        Add(dref,"single reference category accepted",true,new(){["categoryFilter"]="\"surfaces\""});
        Add(dref,"unknown reference category refused",false,new(){["categoryFilter"]="\"everything\""});
        Add(dref,"null reference category refused",false,new(){["categoryFilter"]="null"});
        Add(dref,"zero reference scan limit refused",false,new(){["scanLimit"]="0"});
        Add(dref,"over-limit reference scan refused",false,new(){["scanLimit"]="10001"});

        var trees=new List<SyntaxTree>();
        for(var index=0;index<probes.Count;index++)
        {
            var probe=probes[index];
            var text=File.ReadAllText(Path.Combine(root,"skills",probe.File.Replace('/',Path.DirectorySeparatorChar)));
            var code=Regex.Match(text,"```csharp\\s*\\r?\\n([\\s\\S]*?)\\r?\\n```").Groups[1].Value;
            var boundary=code.IndexOf("// Host access starts here.",StringComparison.Ordinal);
            if(boundary<0) throw new InvalidOperationException(probe.File+" needs a validation/host boundary.");
            var source="using System; using System.Linq; using System.Collections.Generic; public static class Probe"+index+" { public static object Run(){\n"
                +code[..boundary]+"\n"+(probe.Tail??"return new {success=true};")+"\n}}";
            var syntax=CSharpSyntaxTree.ParseText(source,new CSharpParseOptions(LanguageVersion.Latest)).GetRoot();
            foreach(var (name,value) in probe.Values)
            {
                var variable=syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(n=>n.Identifier.ValueText==name);
                syntax=syntax.ReplaceNode(variable.Initializer!.Value,SyntaxFactory.ParseExpression(value));
            }
            trees.Add(CSharpSyntaxTree.Create((CompilationUnitSyntax)syntax,path:probe.Name));
        }
        var compilation=CSharpCompilation.Create("EngineeringSkillInputProbes",trees,references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream=new MemoryStream();
        var emitted=compilation.Emit(stream);
        if(!emitted.Success) throw new InvalidOperationException(string.Join("\n",emitted.Diagnostics.Where(d=>d.Severity==DiagnosticSeverity.Error)));
        stream.Position=0;
        var context=new AssemblyLoadContext("engineering-skill-inputs",isCollectible:true);
        try
        {
            var assembly=context.LoadFromStream(stream);
            for(var index=0;index<probes.Count;index++)
            {
                var probe=probes[index];
                var result=assembly.GetType("Probe"+index)!.GetMethod("Run",BindingFlags.Public|BindingFlags.Static)!.Invoke(null,null)!;
                var succeeded=(bool)Property(result,"success")!;
                if(succeeded!=probe.Expected) throw new InvalidOperationException(probe.Name+": unexpected success="+succeeded);
                probe.Check?.Invoke(result);
            }
        }
        finally {context.Unload();}
        Console.WriteLine($"Engineering skill input/statistics validation passed {probes.Count} host-free cases (native Civil behavior not tested).");

        void Add(string file,string name,bool expected,Dictionary<string,string> values)=>probes.Add(new(name,file,expected,values));
    }
    private static Dictionary<string,string> Change(Dictionary<string,string> original,string key,string value)=>new(original){[key]=value};
    private static object? Property(object value,string name)=>value.GetType().GetProperty(name)!.GetValue(value);
    private static void Near(object value,string name,double expected,double relativeTolerance=1e-10)
    {
        var actual=Convert.ToDouble(Property(value,name));
        if(!Double.IsFinite(actual) || Math.Abs(actual-expected)>relativeTolerance*Math.Max(1,Math.Abs(expected)))
            throw new InvalidOperationException(name+": expected "+expected+", actual "+actual);
    }
    private static void Equal(object value,string name,int expected)
    {
        if(Convert.ToInt32(Property(value,name))!=expected) throw new InvalidOperationException(name+": unexpected count.");
    }
}
