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

        var rowsFile="sections/survey_row_station_candidates.skill.md";
        var rowValues=new Dictionary<string,string>{["alignmentHandle"]="\"AB12\""};
        Add(rowsFile,"placeholder alignment refused",false,new());
        Add(rowsFile,"configured row search accepted",true,rowValues);
        Add(rowsFile,"zero max offset refused",false,Change(rowValues,"maxOffset","0.0"));
        Add(rowsFile,"tiny row gap refused",false,Change(rowValues,"rowGap","0.01"));
        Add(rowsFile,"rounding above row gap refused",false,Change(rowValues,"roundTo","2.0"));
        Add(rowsFile,"zero minimum points refused",false,Change(rowValues,"minPoints","0"));
        Add(rowsFile,"reversed row range refused",false,Change(Change(rowValues,"rangeStart","100.0"),"rangeEnd","50.0"));
        Add(rowsFile,"over-limit row details refused",false,Change(rowValues,"detailLimit","1001"));
        Add(rowsFile,"over-limit point scan refused",false,Change(rowValues,"scanLimit","1000001"));
        Add(rowsFile,"too many point groups refused",false,Change(rowValues,"pointGroupNames",
            "string.Join(\"|\",Enumerable.Range(0,51).Select(i=>\"G\"+i))"));
        // Straight row at 10, a row skewed by slope 0.2 crossing at 30, and one stray point at 20.
        const string rowPoints="var pts=new (double station,double offset,int index)[] {(10.0,-5,0),(10.1,0,1),(9.9,5,2),"
            +"(28.8,-6,3),(29.4,-3,4),(30.0,0,5),(30.6,3,6),(31.2,6,7),(20.0,2,8)};";
        probes.Add(new("rows split at station gaps with skew fit",rowsFile,true,rowValues,
            rowPoints+"var rows=GroupRows(pts); var a=EvaluateRow(rows[0]); var b=EvaluateRow(rows[2]); var c=EvaluateRow(rows[1]);"
            +"return new { success=rows.Count==3 && a.rejection==null && b.rejection==null && c.rejection==\"too few points\" && rows[1].Count==1,"
            +"aStation=a.station, aRms=a.rms, bStation=b.station, bCrossing=b.crossing.Value, bSkew=b.skewDeg, bRms=b.rms, bLeft=b.left, bRight=b.right };",
            result=> {
                Near(result,"aStation",10.0,1e-9); Near(result,"bStation",30.0,1e-9); Near(result,"bCrossing",30.0,1e-9);
                Near(result,"bSkew",Math.Atan(0.2)*180/Math.PI,1e-9); Near(result,"bRms",0,1e-9);
                Equal(result,"bLeft",2); Equal(result,"bRight",2);
            }));
        probes.Add(new("one-sided rows and narrow rows rejected by option",rowsFile,true,Change(rowValues,"requireCrossing","true"),
            "var one=new List<(double station,double offset,int index)>{(50,1,0),(50.2,4,1),(50.1,8,2)};"
            +"var narrow=new List<(double station,double offset,int index)>{(60,-0.5,0),(60,0,1),(60,0.5,2)};"
            +"return new { success=EvaluateRow(one).rejection==\"points on one side only\" && EvaluateRow(narrow).rejection==\"offset span below minOffsetSpan\" };"));
        probes.Add(new("line measured along the road rejected for spread",rowsFile,true,rowValues,
            "var kerb=Enumerable.Range(0,41).Select(i=>(station:i*0.5,offset:5.0+i%2,index:i)).ToList();"
            +"var rows=GroupRows(kerb);"
            +"return new { success=rows.Count==1 && EvaluateRow(rows[0]).rejection==\"station spread above maxRowSpread\" };"));
        probes.Add(new("one-offset row falls back to the median station",rowsFile,true,Change(rowValues,"minOffsetSpan","0.0"),
            "var row=new List<(double station,double offset,int index)>{(70.0,3,0),(70.4,3,1),(70.2,3,2),(70.3,3,3)};"
            +"var r=EvaluateRow(row); return new { success=r.rejection==null && !r.crossing.HasValue, station=r.station };",
            result=>Near(result,"station",70.25,1e-9)));
        probes.Add(new("description wildcards include and exclude",rowsFile,true,
            Change(Change(rowValues,"descriptionFilter","\" SZ* | fo? \""),"excludeDescriptions","\"SZFA*\""),
            "return new { success=descriptionMatches(\"sz12\") && descriptionMatches(\" FOL \") && !descriptionMatches(\"FO\")"
            +" && !descriptionMatches(\"SZFA3\") && !descriptionMatches(\"TE1\") && !descriptionMatches(null) };"));
        probes.Add(new("empty filters keep every description",rowsFile,true,rowValues,
            "return new { success=descriptionMatches(\"anything\") && descriptionMatches(null) && descriptionMatches(\"\") };"));
        probes.Add(new("station rounding",rowsFile,true,Change(rowValues,"roundTo","0.1"),
            "return new { success=true, rounded=RoundStation(30.04), up=RoundStation(30.06) };",
            result=> { Near(result,"rounded",30.0,1e-9); Near(result,"up",30.1,1e-9); }));

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
