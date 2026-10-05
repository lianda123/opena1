using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ExplodeBook.Planning;

internal static class Program
{
    static int passed;
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL: "+name);Console.WriteLine("PASS: "+name);passed++;}
    static Solid Box(double x,double y,double z,double X,double Y,double Z)
    {
        var v=new[]{new Vec(x,y,z),new Vec(X,y,z),new Vec(X,Y,z),new Vec(x,Y,z),new Vec(x,y,Z),new Vec(X,y,Z),new Vec(X,Y,Z),new Vec(x,Y,Z)};
        int[][] f={new[]{0,2,1},new[]{0,3,2},new[]{4,5,6},new[]{4,6,7},new[]{0,1,5},new[]{0,5,4},new[]{1,2,6},new[]{1,6,5},new[]{2,3,7},new[]{2,7,6},new[]{3,0,4},new[]{3,4,7}};
        return new Solid(f.Select(a=>new Tri(v[a[0]],v[a[1]],v[a[2]])));
    }
    static Body B(string id,Solid solid){var b=new Body{Id=id,Name=id};b.Solids.Add(solid);return b;}
    static Solid Rotate(Solid solid,double angle) => new Solid(solid.Triangles.Select(t=>new Tri(Rot(t.A,angle),Rot(t.B,angle),Rot(t.C,angle))));
    static Vec Rot(Vec p,double angle)=>new Vec(p.X*Math.Cos(angle)-p.Y*Math.Sin(angle),p.X*Math.Sin(angle)+p.Y*Math.Cos(angle),p.Z);
    static bool AnalyticSweep(Box a,Box b,Vec displacement)
    {
        double lo=0,hi=1;
        for(int axis=0;axis<3;axis++)
        {
            double speed=displacement.At(axis);
            if(Math.Abs(speed)<1e-12){if(a.Max.At(axis)<=b.Min.At(axis)||a.Min.At(axis)>=b.Max.At(axis))return false;continue;}
            double x=(b.Min.At(axis)-a.Max.At(axis))/speed,y=(b.Max.At(axis)-a.Min.At(axis))/speed;
            if(x>y){double temp=x;x=y;y=temp;}lo=Math.Max(lo,x);hi=Math.Min(hi,y);
        }
        return hi>lo && hi>0 && lo<1;
    }
    static Plan Solve(params Body[] bodies)=>new SequencePlanner(new PairCache(),1e-7,100,"test").Solve(bodies);
    static void Main()
    {
        var a=B("a",Box(0,0,0,1,1,1));var touch=B("touch",Box(1,0,0,2,1,1));var inside=B("inside",Box(.2,.2,.2,.8,.8,.8));
        Check(!Collision.InitialOverlap(a,touch,1e-7),"face contact is allowed");
        Check(Collision.InitialOverlap(a,inside,1e-7),"contained solids are rejected");
        Check(Collision.InitialOverlap(a,B("same",Box(0,0,0,1,1,1)),1e-7),"coincident equal solids are rejected");
        Check(Collision.InitialOverlap(a,B("cross",Box(.5,-1,.5,1.5,2,.6)),1e-7),"transverse overlap is rejected");
        var farA=B("farA",Box(2700,0,0,2701.00024,1,1));var farB=B("farB",Box(2701,0,0,2702,1,1));
        Check(!Collision.InitialOverlap(farA,farB,.001),"0.00024mm rounding at distant world coordinates is treated within contact tolerance");
        Check(Collision.InitialOverlap(B("deep",Box(2700,0,0,2701.02,1,1)),farB,.001),"real 0.02mm penetration still fails with 0.001mm contact tolerance");
        Check(Collision.Swept(farA,farB,new Vec(10,0,0),.001),"contact tolerance cannot permit motion into the adjoining part");
        Check(!Collision.Swept(a,touch,new Vec(-10,0,0),1e-7),"leaving initial contact is allowed");
        Check(Collision.Swept(a,touch,new Vec(10,0,0),1e-7),"moving into contacting solid is blocked");
        var thin=B("thin",Box(5,-1,-1,5.001,2,2));
        Check(Collision.Swept(a,thin,new Vec(10,0,0),1e-7),"0.001-unit blocker cannot be skipped");
        Check(!Collision.Swept(a,thin,new Vec(0,10,0),1e-7),"separated swept boxes are pruned");
        var slide=B("slide",Box(0,0,1,1,1,2));Check(!Collision.Swept(slide,a,new Vec(10,0,0),1e-7),"sliding face contact is allowed");
        var cage=B("cage",Box(-1,-1,-1,4,4,0));
        cage.Solids.Add(Box(-1,-1,0,0,4,3));cage.Solids.Add(Box(3,-1,0,4,4,3));cage.Solids.Add(Box(0,-1,0,3,0,3));cage.Solids.Add(Box(0,3,0,3,4,3));
        cage.ForcedBase=true;var item=B("item",Box(1,1,1,2,2,2));var lid=B("lid",Box(-1,-1,3,4,4,4));
        var plan=Solve(cage,item,lid);Check(plan.Success,"openable enclosure plans successfully");
        Check(plan.Steps.Select(s=>s.Body.Id).SequenceEqual(new[]{"cage","item","lid"}),"payload is installed before lid");
        Check(plan.Steps[1].Outward.Z>.99,"payload uses checked upward removal path");
        lid.Predecessors.Add(item.Id);var constraint=Solve(cage,item,lid);Check(constraint.Success,"consistent partial order is respected");
        lid.Predecessors.Clear();item.Predecessors.Add(lid.Id);var wrong=Solve(cage,item,lid);Check(!wrong.Success&&wrong.Steps.Count==0,"wrong lid-first manual order stops output");item.Predecessors.Clear();
        cage.Solids.Add(lid.Solids[0]);var locked=Solve(cage,item);Check(!locked.Success&&locked.Blocked["item"].Contains("cage"),"fully enclosed payload reports blocker");
        Check(!Solve(a,inside).Success,"invalid assembled overlap cannot generate a plan");
        var d1=B("first",Box(0,0,0,1,1,1));var d2=B("second",Box(3,0,0,4,1,1));d1.ManualOrder=1;d2.ManualOrder=1;Check(!Solve(d1,d2).Success,"duplicate manual ranks are rejected");
        d1.ManualOrder=d2.ManualOrder=0;d1.Predecessors.Add(d2.Id);d2.Predecessors.Add(d1.Id);Check(!Solve(d1,d2).Success,"cyclic user precedence is rejected");
        Check(!new SequencePlanner(new PairCache(),1e-7,100,"test",()=>true).Solve(new[]{a,touch}).Success,"cancellation returns no successful plan");
        Check(!new SequencePlanner(new PairCache(),1e-7,100,"test",null,()=>"budget").Solve(new[]{a,touch}).Success,"time budget returns no successful plan");
        var cache=new PairCache();var planner=new SequencePlanner(cache,1e-7,100,"test");var one=planner.Solve(new[]{a,touch});var two=planner.Solve(new[]{a,touch});Check(one.Success&&two.Success&&two.PairTests==0&&two.CacheHits>0,"repeated analysis reuses pair checks");
        var rot=new Vec(.7071067811865476,.7071067811865476,0);var obstacle=B("diag",Box(4,4,-1,5,5,2));Check(Collision.Swept(a,obstacle,rot*10,1e-7),"diagonal continuous collision is detected");
        var slotted=B("slotted",Box(0,0,0,2,4,10));slotted.Solids.Add(Box(0,6,0,2,10,10));slotted.Solids.Add(Box(0,4,0,2,6,4));slotted.Solids.Add(Box(0,4,6,2,6,10));slotted.ForcedBase=true;
        var tab=B("tab",Box(-2,4,4,4,6,6));tab.Directions.Add(new Vec(1,0,0));
        Check(Solve(slotted,tab).Success,"exact-fitting tab passes through true slot, not bounding box");
        double angle=Math.PI/6;var rotatedFrame=new Body{Id="rotFrame",Name="rotFrame",ForcedBase=true};rotatedFrame.Solids.AddRange(slotted.Solids.Select(x=>Rotate(x,angle)));
        var rotatedTab=B("rotTab",Rotate(tab.Solids[0],angle));Check(!Solve(rotatedFrame,rotatedTab).Success,"world-only directions cannot remove exact-fitting rotated tab");rotatedTab.Directions.Add(Rot(new Vec(1,0,0),angle));Check(Solve(rotatedFrame,rotatedTab).Success,"local normal recovers rotated insertion path");
        var random=new Random(12345);bool randomized=true;
        for(int i=0;i<250;i++)
        {
            double x=random.NextDouble()*8-4,y=random.NextDouble()*8-4,z=random.NextDouble()*8-4;
            var other=B("random",Box(x,y,z,x+.2+random.NextDouble()*2,y+.2+random.NextDouble()*2,z+.2+random.NextDouble()*2));
            if(Collision.InitialOverlap(a,other,1e-7))continue;
            var move=new Vec(random.NextDouble()*20-10,random.NextDouble()*20-10,random.NextDouble()*20-10);
            if(Collision.Swept(a,other,move,1e-7)!=AnalyticSweep(a.Bounds,other.Bounds,move)){randomized=false;Console.WriteLine("Random mismatch "+i);break;}
        }
        Check(randomized,"250 seeded swept-box cases match independent analytic intervals");
        bool scaled=true;foreach(double scale in new[]{.001,1.0,1000.0}){var aa=B("scaleA",Box(0,0,0,scale,scale,scale));var bb=B("scaleB",Box(5*scale,-scale,-scale,5.001*scale,2*scale,2*scale));scaled &= Collision.Swept(aa,bb,new Vec(10*scale,0,0),1e-9*scale);}
        Check(scaled,"thin collision detection is invariant across metre/mm-scale geometry");
        var many=new List<Body>();for(int i=0;i<150;i++)many.Add(B("part"+i.ToString("000"),Box(i*3,0,0,i*3+1,1,1)));
        var sw=Stopwatch.StartNew();var large=new SequencePlanner(new PairCache(),1e-7,1000,"bench").Solve(many);sw.Stop();Check(large.Success&&large.Steps.Count==150,"150-part plan contains every physical part");Console.WriteLine("BENCHMARK 150 parts: "+sw.ElapsedMilliseconds+" ms, pair tests="+large.PairTests+", cache hits="+large.CacheHits);
        var chain=new List<Body>();for(int i=0;i<150;i++)chain.Add(B("chain"+i.ToString("000"),Box(i,0,0,i+1,1,1)));
        var chainCache=new PairCache();var chainPlanner=new SequencePlanner(chainCache,1e-7,1000,"chain");sw.Restart();var cold=chainPlanner.Solve(chain);long coldMs=sw.ElapsedMilliseconds;sw.Restart();var warm=chainPlanner.Solve(chain);long warmMs=sw.ElapsedMilliseconds;
        Check(cold.Success&&warm.Success&&warm.PairTests==0,"150 touching parts reuse initial and swept collision checks");
        Console.WriteLine("BENCHMARK touching chain: cold="+coldMs+" ms, warm="+warmMs+" ms, cold pairs="+cold.PairTests+", warm pairs="+warm.PairTests+", warm cache hits="+warm.CacheHits);
        Console.WriteLine("TOTAL "+passed+" passed");
    }
}
