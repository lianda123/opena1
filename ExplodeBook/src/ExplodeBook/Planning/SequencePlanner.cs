using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ExplodeBook.Planning;

internal sealed class PlanStep
{
    public Body Body;
    public Vec Outward;
    public double Travel;
}
internal sealed class Plan
{
    public readonly List<PlanStep> Steps=new List<PlanStep>();
    public readonly Dictionary<string,List<string>> Blocked=new Dictionary<string,List<string>>();
    public readonly List<string> Errors=new List<string>();
    public bool Success => Errors.Count==0&&Blocked.Count==0&&Steps.Count>0;
    public long PairTests,CacheHits;
}

internal sealed class PairCache
{
    private readonly Dictionary<string,bool> values=new Dictionary<string,bool>();
    public int Count=>values.Count;
    public bool Get(string key,Func<bool> calculate,out bool hit)
    {
        if(values.TryGetValue(key,out bool value)){hit=true;return value;}
        hit=false;value=calculate();values[key]=value;return value;
    }
    public void Clear()=>values.Clear();
}

internal sealed class SequencePlanner
{
    public static readonly Vec[] WorldDirections={new Vec(0,0,1),new Vec(0,0,-1),new Vec(1,0,0),new Vec(-1,0,0),new Vec(0,1,0),new Vec(0,-1,0)};
    private readonly PairCache cache;
    private readonly double epsilon,travel;
    private readonly Func<bool> cancel;
    private readonly Func<string> budgetExceeded;
    private readonly string scope;
    public SequencePlanner(PairCache cache,double epsilon,double travel,string scope,Func<bool> cancel=null,Func<string> budgetExceeded=null)
    {this.cache=cache;this.epsilon=epsilon;this.travel=travel;this.scope=scope;this.cancel=cancel;this.budgetExceeded=budgetExceeded;}

    public Plan Solve(IList<Body> bodies)
    {
        var plan=new Plan();if(bodies.Count==0){plan.Errors.Add("没有实体零件");return plan;}
        if(bodies.Any(b=>!b.Valid)){plan.Errors.Add("存在无有效封闭几何的零件");return plan;}
        if(bodies.Count(b=>b.ForcedBase)>1){plan.Errors.Add("同一装配范围内存在多个基准件");return plan;}
        var duplicate=bodies.Where(b=>b.ManualOrder>0).GroupBy(b=>b.ManualOrder).FirstOrDefault(g=>g.Count()>1);
        if(duplicate!=null){plan.Errors.Add("手动顺序编号重复："+duplicate.Key);return plan;}
        try
        {
            // Validate the assembled state, including containment, before planning.
            foreach(var pair in SpatialPairs(bodies,epsilon))
            {
                Guard();var a=pair.Item1;var b=pair.Item2;
                bool overlap=cache.Get(scope+"|initial|"+a.Id+"|"+b.Id,()=>Collision.InitialOverlap(a,b,epsilon,cancel),out bool hit);
                if(hit)plan.CacheHits++;else plan.PairTests++;
                if(overlap){AddBlocked(plan,a.Id,b.Id);AddBlocked(plan,b.Id,a.Id);plan.Errors.Add("最终位置存在实体重叠："+a.Name+" / "+b.Name);}
            }
            if(plan.Errors.Count>0)return plan;
            var active=bodies.ToList();var removed=new List<PlanStep>();
            Body forced=bodies.FirstOrDefault(b=>b.ForcedBase);
            if(forced!=null&&(forced.Predecessors.Count>0||bodies.Any(b=>b.ManualOrder>0&&b.ManualOrder<forced.ManualOrder))){plan.Errors.Add("基准件与手动先后顺序冲突");return plan;}
            while(active.Count>1)
            {
                Guard();PlanStep selected=null;
                // Only eligible, collision-free removals are committed. Removal
                // monotonically removes blockers, so no exponential permutation search.
                foreach(var body in active.Where(b=>b!=forced).OrderByDescending(b=>b.ManualOrder).ThenByDescending(b=>b.Bounds.Center.Z).ThenBy(b=>b.Id,StringComparer.Ordinal))
                {
                    if(body.ManualOrder>0&&active.Any(b=>b!=body&&b.ManualOrder>body.ManualOrder))continue;
                    if(active.Any(b=>b!=body&&b.Predecessors.Contains(body.Id)))continue;
                    var obstacles=active.Where(b=>b!=body).ToList();
                    selected=FindExit(body,obstacles,plan,null);
                    if(selected!=null)break;
                }
                if(selected==null)
                {
                    foreach(var body in active.Where(b=>b!=forced))FindExit(body,active.Where(b=>b!=body).ToList(),plan,plan.Blocked);
                    plan.Errors.Add("剩余零件在已检查直线方向中互锁，或手动顺序有冲突；没有补造后续步骤。");return plan;
                }
                removed.Add(selected);active.Remove(selected.Body);
            }
            plan.Steps.Add(new PlanStep{Body=active[0],Travel=0});
            removed.Reverse();plan.Steps.AddRange(removed);
            // Forward pass: check every printed insertion against exactly the
            // components already installed, using the same cached path predicates.
            var installed=new List<Body>{active[0]};
            foreach(var step in removed)
            {
                var check=FindExit(step.Body,installed,plan,null,new[]{step.Outward});
                if(check==null){plan.Errors.Add("反向复核失败："+step.Body.Name);plan.Steps.Clear();return plan;}installed.Add(step.Body);
            }
            return plan;
        }
        catch(OperationCanceledException){plan.Errors.Add("分析已取消，未生成说明书");plan.Steps.Clear();return plan;}
        catch(TimeoutException e){plan.Errors.Add(e.Message);plan.Steps.Clear();return plan;}
    }

    private PlanStep FindExit(Body moving,IList<Body> obstacles,Plan plan,Dictionary<string,List<string>> diagnostics,IEnumerable<Vec> fixedDirections=null)
    {
        var directions=fixedDirections??(moving.Directions.Count>0?moving.Directions:WorldDirections.AsEnumerable());
        var blockers=new HashSet<string>();
        foreach(var direction in directions)
        {
            Guard();Vec d=direction.Unit;if(d.Length<.5)continue;Vec move=d*travel;bool blocked=false;
            foreach(var obstacle in obstacles.Where(b=>moving.Bounds.Sweep(move).Overlaps(b.Bounds,epsilon)).OrderBy(b=>moving.Bounds.Gap(b.Bounds)))
            {
                string key=scope+"|sweep|"+moving.Id+"|"+obstacle.Id+"|"+d.Key;
                bool collision=cache.Get(key,()=>Collision.Swept(moving,obstacle,move,epsilon,cancel),out bool hit);
                if(hit)plan.CacheHits++;else plan.PairTests++;
                if(collision){blocked=true;blockers.Add(obstacle.Id);if(diagnostics==null)break;}
            }
            if(!blocked)return new PlanStep{Body=moving,Outward=d,Travel=travel};
        }
        if(diagnostics!=null)diagnostics[moving.Id]=blockers.ToList();return null;
    }
    private void Guard()
    {
        if(cancel!=null&&cancel())throw new OperationCanceledException();
        string error=budgetExceeded?.Invoke();if(error!=null)throw new TimeoutException(error);
    }
    private static void AddBlocked(Plan plan,string a,string b){if(!plan.Blocked.TryGetValue(a,out var list))plan.Blocked[a]=list=new List<string>();if(!list.Contains(b))list.Add(b);}
    public static IEnumerable<Tuple<Body,Body>> SpatialPairs(IList<Body> bodies,double e)
    {
        var sorted=bodies.OrderBy(b=>b.Bounds.Min.X).ToList();
        for(int i=0;i<sorted.Count;i++)for(int j=i+1;j<sorted.Count&&sorted[j].Bounds.Min.X<=sorted[i].Bounds.Max.X+e;j++)
            if(sorted[i].Bounds.Overlaps(sorted[j].Bounds,e))yield return Tuple.Create(sorted[i],sorted[j]);
    }
}
