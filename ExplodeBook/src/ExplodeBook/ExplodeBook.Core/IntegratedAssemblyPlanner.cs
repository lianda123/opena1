using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ExplodeBook.Planning;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ExplodeBook.Core;

internal sealed class PlanningSession
{
    public readonly Dictionary<string,List<Solid>> Meshes=new Dictionary<string,List<Solid>>();
    public readonly PairCache Pairs=new PairCache();
    public AssemblyAnalysis Last;
    public List<Guid> Sources=new List<Guid>();
    public long MeshBuilds,MeshHits;
    public string BookSignature;
}

internal static class IntegratedAssemblyPlanner
{
    private static readonly Dictionary<uint,PlanningSession> Sessions=new Dictionary<uint,PlanningSession>();
    public static PlanningSession Session(RhinoDoc doc)
    {if(!Sessions.TryGetValue(doc.RuntimeSerialNumber,out var s))Sessions[doc.RuntimeSerialNumber]=s=new PlanningSession();return s;}
    public static void Close(uint serial)=>Sessions.Remove(serial);
    public static Vec V(Point3d p)=>new Vec(p.X,p.Y,p.Z);
    public static Vec V(Vector3d p)=>new Vec(p.X,p.Y,p.Z);
    public static Vector3d R(Vec p)=>new Vector3d(p.X,p.Y,p.Z);
    private static string Hash(string text)
    {using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-","");}
    private static string Format(double d)=>d.ToString("R",CultureInfo.InvariantCulture);

    public static void Apply(RhinoDoc doc,AssemblyAnalysis analysis,ExplodeSettings settings)
    {
        using var cancellationScope=AnalysisCancellation.Begin();
        var session=Session(doc);session.Last=analysis;session.Sources=analysis.Parts.SelectMany(p=>p.Objects).Select(o=>o.Id).ToList();
        if(analysis.ValidationErrors.Count>0)return;
        var timer=Stopwatch.StartNew();long builds=session.MeshBuilds,hits=session.MeshHits;
        double units=settings.ModelUnitsPerMillimeter;
        double error=settings.MeshToleranceMillimeters*units;
        double epsilon=Math.Max(1e-9*units,Math.Min(doc.ModelAbsoluteTolerance,0.001*units));
        double travel=Math.Max(analysis.Bounds.Diagonal.Length*2,100*units);
        Func<string> budget=()=>timer.Elapsed.TotalSeconds>settings.AnalysisTimeoutSeconds?"分析超过时间上限，保留诊断且停止出图；可拆分模块或提高分析时间。":null;
        Func<bool> cancel=()=> {string exceeded=budget();if(exceeded!=null)throw new TimeoutException(exceeded);return AnalysisCancellation.Check();};
        string scope=Format(epsilon)+"|"+Format(travel);
        var bodies=new Dictionary<AssemblyPart,Body>();
        try
        {
            foreach(var part in analysis.Parts)
            {
                analysis.ProgressSummary="处理零件 "+(bodies.Count+analysis.PartIssues.Count+1)+"/"+analysis.Parts.Count+"："+part.PartNumber;
                if(cancel())throw new OperationCanceledException();if(budget()!=null)throw new TimeoutException(budget());
                try {
                var body=BuildBody(part,doc,session,error);
                body.ForcedBase=part.IsForcedBase;
                if(part.HasExactExplosionOffset&&part.HasCustomDirection&&Math.Abs(V(part.ExactExplosionOffset).Unit.Dot(V(part.CustomDirection).Unit)-1)>1e-6)
                    throw new InvalidOperationException(part.PartNumber+"：手动爆炸位置与手动方向冲突。");
                if(part.HasExactExplosionOffset)body.Directions.Add(V(part.ExactExplosionOffset).Unit);
                else if(part.HasCustomDirection)body.Directions.Add(V(part.CustomDirection).Unit);
                else
                {
                    body.Directions.AddRange(SequencePlanner.WorldDirections);
                    // Face-normal candidates recover rotated board insertion paths.
                    foreach(var n in body.Solids.SelectMany(s=>s.Triangles).OrderByDescending(t=>t.Normal.Length).Select(t=>t.Normal.Unit))
                    {
                        if(body.Directions.All(d=>Math.Abs(d.Dot(n))<1-1e-7)){body.Directions.Add(n);body.Directions.Add(-n);}
                        if(body.Directions.Count>=18)break;
                    }
                }
                bodies[part]=body;
                }
                catch(OperationCanceledException){throw;}
                catch(TimeoutException){throw;}
                catch(Exception ex)
                {
                    string label=ex.Message.Contains("25万")?"网格过密":ex.Message.Contains("分析网格")?"网格生成失败":ex.Message.Contains("方向冲突")?"方向冲突":ex.Message.Contains("未封闭")||ex.Message.Contains("无效")?"实体需检查":"分析失败";
                    analysis.PartIssues[part]=label;analysis.ValidationErrors.Add(ex.Message.StartsWith(part.PartNumber,StringComparison.Ordinal)?ex.Message:part.PartNumber+"："+ex.Message);
                }
            }
            if(analysis.ValidationErrors.Count>0)return;
            // Final assembly overlap is checked across module boundaries as well.
            analysis.ProgressSummary="检查最终装配位置";
            var all=bodies.Values.ToList();
            var preliminary=new Plan();
            foreach(var pair in SequencePlanner.SpatialPairs(all,epsilon))
            {
                if(cancel())throw new OperationCanceledException();if(budget()!=null)throw new TimeoutException(budget());
                bool overlap=session.Pairs.Get(scope+"|initial|"+pair.Item1.Id+"|"+pair.Item2.Id,()=>Collision.InitialOverlap(pair.Item1,pair.Item2,epsilon,cancel),out bool cached);
                if(cached)analysis.CacheHits++;else analysis.PairTests++;
                if(overlap){preliminary.Errors.Add("最终装配位置有实体重叠："+pair.Item1.Name+" / "+pair.Item2.Name);AddBlocker(preliminary,pair.Item1.Id,pair.Item2.Id);AddBlocker(preliminary,pair.Item2.Id,pair.Item1.Id);}
            }
            if(preliminary.Errors.Count>0){Record(analysis,preliminary,bodies);return;}
            analysis.Modules.Clear();
            foreach(var group in analysis.Parts.GroupBy(p=>string.IsNullOrWhiteSpace(p.Subassembly)?"主体模块":p.Subassembly.Trim(),StringComparer.OrdinalIgnoreCase))
            {
                analysis.ProgressSummary="检查模块内部："+group.Key;
                var module=new AssemblyModule{Name=group.Key,Sequence=analysis.Modules.Count+1};
                module.Parts.AddRange(group);module.Bounds=group.Select(p=>p.Bounds).Aggregate(BoundingBox.Union);module.SizeScore=group.Sum(p=>p.SizeScore);analysis.Modules.Add(module);
                var local=group.Select(p=>bodies[p]).ToList();
                AddOrderConstraints(group.ToList(),bodies,p=>p.ModulePartOrder,"模块内",analysis);
                AddOrderConstraints(group.ToList(),bodies,p=>p.AssemblyOrder,"零件",analysis);
                if(analysis.ValidationErrors.Count>0)return;
                var planner=new SequencePlanner(session.Pairs,epsilon,travel,scope,cancel,budget);
                var plan=planner.Solve(local);analysis.PairTests+=plan.PairTests;analysis.CacheHits+=plan.CacheHits;
                if(!plan.Success){Record(analysis,plan,bodies);return;}
                int order=0;
                foreach(var step in plan.Steps)
                {
                    var part=group.First(p=>bodies[p]==step.Body);part.ModulePartOrder=++order;part.IsModuleBase=order==1;part.AutoDirection=R(step.Outward);part.PathVerified=true;part.PathTravel=step.Travel;
                }
                module.Parts.Sort((a,b)=>a.ModulePartOrder.CompareTo(b.ModulePartOrder));
            }
            var moduleBodies=new Dictionary<AssemblyModule,Body>();
            foreach(var module in analysis.Modules)
            {
                var body=new Body{Name=module.Name,Id="module:"+Hash(string.Join(";",module.Parts.Select(p=>bodies[p].Id).OrderBy(x=>x)))};
                body.Solids.AddRange(module.Parts.SelectMany(p=>bodies[p].Solids));body.ForcedBase=module.Parts.Any(p=>p.IsForcedBase);
                var moduleDirections=module.Parts.SelectMany(p=>p.Objects).Select(o=>o.Attributes.GetUserString("EB2.ModuleDirection")).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct().ToList();
                if(moduleDirections.Count>1){analysis.ValidationErrors.Add(module.Name+"：模块手动方向不一致。");return;}
                if(moduleDirections.Count==1) {if(!ParseDirection(moduleDirections[0],out var customModuleDirection))throw new InvalidOperationException(module.Name+"：模块方向参数无效。");body.Directions.Add(V(customModuleDirection).Unit);}
                else body.Directions.AddRange(SequencePlanner.WorldDirections);
                if(moduleDirections.Count==0) foreach(var direction in module.Parts.SelectMany(p=>bodies[p].Directions))if(body.Directions.All(d=>d.Dot(direction)<1-1e-7))body.Directions.Add(direction);
                var manual=module.Parts.Select(p=>ReadInt(p,"ExplodeBook.ModuleOrder")).Where(n=>n>0).Distinct().ToList();
                if(manual.Count>1){analysis.ValidationErrors.Add(module.Name+"：同一模块存在不同手动总装顺序。");return;}
                body.ManualOrder=manual.Count==1?manual[0]:0;moduleBodies[module]=body;
            }
            // Global manual part ranks also constrain whole-module assembly.
            foreach(var a in analysis.Modules)foreach(var b in analysis.Modules.Where(m=>m!=a))
                if(a.Parts.Any(p=>ReadInt(p,"ExplodeBook.Order")>0&&b.Parts.Any(q=>ReadInt(q,"ExplodeBook.Order")>ReadInt(p,"ExplodeBook.Order"))))moduleBodies[b].Predecessors.Add(moduleBodies[a].Id);
            analysis.ProgressSummary="检查模块总装路径";
            var modulePlan=new SequencePlanner(session.Pairs,epsilon,travel,scope,cancel,budget).Solve(moduleBodies.Values.ToList());
            analysis.PairTests+=modulePlan.PairTests;analysis.CacheHits+=modulePlan.CacheHits;
            if(!modulePlan.Success)
            {
                analysis.ValidationErrors.AddRange(modulePlan.Errors);
                foreach(var entry in modulePlan.Blocked)
                {
                    var a=moduleBodies.First(m=>m.Value.Id==entry.Key).Key;
                    var blocked=moduleBodies.Where(m=>entry.Value.Contains(m.Value.Id)).SelectMany(m=>m.Key.Parts).ToList();
                    foreach(var part in a.Parts){analysis.Blockers[part]=blocked;analysis.PartIssues[part]="模块装入受阻";}
                }
                return;
            }
            int moduleOrder=0,partOrder=0;
            analysis.Parts.Clear();analysis.Modules.Clear();
            foreach(var step in modulePlan.Steps)
            {
                var module=moduleBodies.First(x=>x.Value==step.Body).Key;module.AssemblyOrder=++moduleOrder;module.Number="M"+moduleOrder.ToString("00");module.IsBase=moduleOrder==1;module.AutoDirection=R(step.Outward);module.PathVerified=true;module.PathTravel=step.Travel;
                analysis.Modules.Add(module);
                foreach(var part in module.Parts){part.Subassembly=module.Name;part.ModuleOrder=moduleOrder;part.AssemblyOrder=++partOrder;part.IsBase=module.IsBase&&part.IsModuleBase;analysis.Parts.Add(part);}
            }
            analysis.BasePart=analysis.Parts[0];analysis.PlanVerified=true;
            analysis.Signature=Hash(string.Join(";",analysis.Parts.Select(p=>bodies[p].Id+":"+p.PartNumber+":"+p.Name+":"+p.AssemblyOrder+":"+p.ModuleOrder+":"+V(p.AutoDirection).Key+":"+string.Join(",",p.Objects.Select(o=>o.Attributes.Name+":"+o.Attributes.ObjectColor.ToArgb()+":"+o.Attributes.ColorSource+":"+o.Attributes.LayerIndex+":"+doc.Layers[o.Attributes.LayerIndex].Color.ToArgb()))))+string.Join(";",analysis.Modules.Select(m=>m.Name+":"+V(m.AutoDirection).Key))+LinkedBookManager.Serialize(settings));
        }
        catch(Exception ex)
        {analysis.PlanVerified=false;analysis.ValidationErrors.Add((ex is OperationCanceledException?"分析已取消，未生成说明书。":ex.Message)+(string.IsNullOrEmpty(analysis.ProgressSummary)?"":"（进度："+analysis.ProgressSummary+"）"));}
        finally
        {
            analysis.AnalysisMilliseconds=timer.ElapsedMilliseconds;analysis.MeshBuilds=session.MeshBuilds-builds;analysis.MeshHits=session.MeshHits-hits;
            if(session.Meshes.Count>2048){session.Meshes.Clear();session.Pairs.Clear();}
            if(session.Pairs.Count>200000)session.Pairs.Clear();
        }
    }
    private static bool ParseDirection(string raw,out Vector3d vector)
    {
        vector=Vector3d.Unset;var parts=raw.Split(',');
        if(parts.Length!=3 || !double.TryParse(parts[0],NumberStyles.Float,CultureInfo.InvariantCulture,out double x) || !double.TryParse(parts[1],NumberStyles.Float,CultureInfo.InvariantCulture,out double y) || !double.TryParse(parts[2],NumberStyles.Float,CultureInfo.InvariantCulture,out double z))return false;
        vector=new Vector3d(x,y,z);return vector.IsValid&&vector.Unitize();
    }
    private static int ReadInt(AssemblyPart part,string key)=>part.Objects.Select(o=>{int.TryParse(o.Attributes.GetUserString(key),out int n);return n;}).FirstOrDefault(n=>n>0);
    private static void AddOrderConstraints(List<AssemblyPart> parts,Dictionary<AssemblyPart,Body> bodies,Func<AssemblyPart,int> rank,string label,AssemblyAnalysis analysis)
    {
        var tagged=parts.Where(p=>rank(p)>0).ToList();
        if(tagged.GroupBy(rank).Any(g=>g.Count()>1)){analysis.ValidationErrors.Add(label+"手动顺序重复，请用 EBSetPartOrder 或 EBAutoOrder 修正。");return;}
        foreach(var a in tagged)foreach(var b in tagged.Where(p=>rank(p)>rank(a)))bodies[b].Predecessors.Add(bodies[a].Id);
    }
    private static void Record(AssemblyAnalysis analysis,Plan plan,Dictionary<AssemblyPart,Body> bodies)
    {
        analysis.ValidationErrors.AddRange(plan.Errors);
        foreach(var entry in plan.Blocked)
        {
            var part=bodies.First(p=>p.Value.Id==entry.Key).Key;
            analysis.Blockers[part]=bodies.Where(p=>entry.Value.Contains(p.Value.Id)).Select(p=>p.Key).ToList();
            analysis.PartIssues[part]=plan.Errors.Any(e=>e.Contains("重叠"))?"位置重叠，需核对":"路径受阻";
        }
    }
    private static void AddBlocker(Plan plan,string part,string obstacle)
    {if(!plan.Blocked.TryGetValue(part,out var list))plan.Blocked[part]=list=new List<string>();if(!list.Contains(obstacle))list.Add(obstacle);}
    private static Body BuildBody(AssemblyPart part,RhinoDoc doc,PlanningSession cache,double error)
    {
        var geometry=new List<GeometryBase>();var signature=new StringBuilder();
        try {
        foreach(var obj in part.Objects.Where(PartResolver.Physical))Collect(obj,Transform.Identity,geometry,signature,new HashSet<Guid>(),0);
        string key=Hash(signature+"|local-mesh-v201|"+Format(error));
        if(!cache.Meshes.TryGetValue(key,out var solids))
        {
            // Keep tessellation close to the origin. Single-precision mesh
            // vertices at large world coordinates otherwise invent penetration.
            var origin=geometry.Select(g=>g.GetBoundingBox(true)).Aggregate(BoundingBox.Union).Center;
            var local=Transform.Translation(-V(origin).X,-V(origin).Y,-V(origin).Z);
            foreach(var g in geometry){if(g is Mesh original)original.Vertices.UseDoublePrecisionVertices=true;if(!g.Transform(local))throw new InvalidOperationException(part.PartNumber+"：分析副本变换失败。");}
            solids=new List<Solid>();
            foreach(var g in geometry)
            {
                Mesh mesh=null;Brep converted=null;
                try {
                if(AnalysisCancellation.Check())throw new OperationCanceledException();
                if(g is Mesh source){if(!source.IsValid||!source.IsClosed)throw new InvalidOperationException(part.PartNumber+"：网格未封闭或无效。");mesh=source.DuplicateMesh();}
                else
                {
                    Brep brep=g as Brep;
                    if(brep==null){converted=(g as Extrusion)?.ToBrep()??(g as SubD)?.ToBrep();brep=converted;}
                    if(brep==null||!brep.IsValid||!brep.IsSolid)throw new InvalidOperationException(part.PartNumber+"：曲面未封闭或实体无效；请先修复实体。");
                    mesh=CreateAnalysisMesh(brep,error,true);
                    if(mesh==null||!mesh.IsClosed||!mesh.IsValid)
                    {
                        mesh?.Dispose();mesh=null;
                        if(AnalysisCancellation.Check())throw new OperationCanceledException();
                        mesh=CreateAnalysisMesh(brep,error*.5,false);
                    }
                    if(mesh==null||!mesh.IsClosed||!mesh.IsValid)throw new InvalidOperationException(part.PartNumber+"：原实体有效封闭，但两次生成的分析网格仍未闭合；停止排序，请检查该件的微小边或修剪边，不要修复全部板件。");
                }
                mesh.Faces.ConvertQuadsToTriangles();
                if(mesh.Faces.Count>250000)throw new InvalidOperationException(part.PartNumber+"：网格超过25万三角面，请简化该零件或提高网格容差。");
                var triangles=new List<Tri>();foreach(var f in mesh.Faces)triangles.Add(new Tri(V(mesh.Vertices.Point3dAt(f.A))+V(origin),V(mesh.Vertices.Point3dAt(f.B))+V(origin),V(mesh.Vertices.Point3dAt(f.C))+V(origin)));
                solids.Add(new Solid(triangles));
                }finally {mesh?.Dispose();converted?.Dispose();}
            }
            cache.Meshes[key]=solids;cache.MeshBuilds++;
        }
        else cache.MeshHits++;
        var body=new Body{Id=part.Objects.First(PartResolver.Physical).Id+":"+key,Name=part.PartNumber+"（"+part.Name+"）"};body.Solids.AddRange(solids);
        if(!body.Valid)throw new InvalidOperationException(part.PartNumber+"：没有可分析的封闭实体。");
        return body;
        } finally {foreach(var g in geometry)g.Dispose();}
    }
    private static Mesh CreateAnalysisMesh(Brep brep,double error,bool simplePlanes)
    {
        using var mp=new MeshingParameters(0.5){Tolerance=error,RelativeTolerance=0,RefineGrid=true,SimplePlanes=simplePlanes,JaggedSeams=false,ClosedObjectPostProcess=true};
#if RHINO8
        mp.DoublePrecision=true;
#endif
        var pieces=Mesh.CreateFromBrep(brep,mp);
        if(pieces==null||pieces.Length==0)return null;
        var mesh=new Mesh();mesh.Vertices.UseDoublePrecisionVertices=true;
        try {foreach(var piece in pieces)mesh.Append(piece);mesh.Vertices.CombineIdentical(true,true);mesh.Weld(Math.PI);mesh.UnifyNormals();return mesh;}
        catch {mesh.Dispose();throw;}
        finally {foreach(var piece in pieces)piece?.Dispose();}
    }
    private static void Collect(RhinoObject obj,Transform transform,List<GeometryBase> output,StringBuilder signature,HashSet<Guid> stack,int depth)
    {
        if(depth>32)throw new InvalidOperationException("块嵌套超过32层，请展开或简化。");
        signature.Append(obj.Id).Append(':').Append(obj.Geometry.DataCRC(0)).Append(':');
        for(int i=0;i<4;i++)for(int j=0;j<4;j++)signature.Append(Format(transform[i,j])).Append(',');
        if(obj is InstanceObject instance)
        {
            var def=instance.InstanceDefinition;if(def==null||!stack.Add(def.Id))throw new InvalidOperationException("块定义缺失或循环嵌套。");
            foreach(var child in def.GetObjects().Where(PartResolver.Physical))Collect(child,transform*instance.InstanceXform,output,signature,stack,depth+1);
            stack.Remove(def.Id);return;
        }
        var duplicate=obj.Geometry.Duplicate();if(!duplicate.Transform(transform)){duplicate.Dispose();throw new InvalidOperationException("实体变换失败。");}output.Add(duplicate);
    }
}
