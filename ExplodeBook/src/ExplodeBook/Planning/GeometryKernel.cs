using System;
using System.Collections.Generic;
using System.Linq;

namespace ExplodeBook.Planning;

// Host-independent geometry kernel. All units are document units.
internal struct Vec
{
    public double X, Y, Z;
    public Vec(double x, double y, double z) { X=x; Y=y; Z=z; }
    public static Vec operator +(Vec a, Vec b) => new Vec(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    public static Vec operator -(Vec a, Vec b) => new Vec(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    public static Vec operator -(Vec a) => new Vec(-a.X,-a.Y,-a.Z);
    public static Vec operator *(Vec a, double b) => new Vec(a.X*b,a.Y*b,a.Z*b);
    public double Dot(Vec b) => X*b.X+Y*b.Y+Z*b.Z;
    public Vec Cross(Vec b) => new Vec(Y*b.Z-Z*b.Y,Z*b.X-X*b.Z,X*b.Y-Y*b.X);
    public double Length => Math.Sqrt(Dot(this));
    public Vec Unit => Length>1e-15 ? this*(1/Length) : new Vec();
    public double At(int i) => i==0 ? X : i==1 ? Y : Z;
    public string Key => string.Join(",", new[]{X,Y,Z}.Select(x=>Math.Round(x,8).ToString("R",System.Globalization.CultureInfo.InvariantCulture)));
}

internal struct Box
{
    public Vec Min,Max;
    public Box(Vec a,Vec b) { Min=a;Max=b; }
    public Vec Center => (Min+Max)*.5;
    public Vec Size => Max-Min;
    public Box Union(Box b) => new Box(new Vec(Math.Min(Min.X,b.Min.X),Math.Min(Min.Y,b.Min.Y),Math.Min(Min.Z,b.Min.Z)),new Vec(Math.Max(Max.X,b.Max.X),Math.Max(Max.Y,b.Max.Y),Math.Max(Max.Z,b.Max.Z)));
    public Box Move(Vec v) => new Box(Min+v,Max+v);
    public Box Sweep(Vec v) => Union(Move(v));
    public bool Overlaps(Box b,double e) => Min.X<=b.Max.X+e && Max.X>=b.Min.X-e && Min.Y<=b.Max.Y+e && Max.Y>=b.Min.Y-e && Min.Z<=b.Max.Z+e && Max.Z>=b.Min.Z-e;
    public bool InteriorOverlaps(Box b,double e) => Math.Min(Max.X,b.Max.X)-Math.Max(Min.X,b.Min.X)>e && Math.Min(Max.Y,b.Max.Y)-Math.Max(Min.Y,b.Min.Y)>e && Math.Min(Max.Z,b.Max.Z)-Math.Max(Min.Z,b.Min.Z)>e;
    public bool Contains(Vec p,double e) => p.X>=Min.X-e&&p.X<=Max.X+e&&p.Y>=Min.Y-e&&p.Y<=Max.Y+e&&p.Z>=Min.Z-e&&p.Z<=Max.Z+e;
    public double Gap(Box b) { double x=Math.Max(0,Math.Max(Min.X-b.Max.X,b.Min.X-Max.X));double y=Math.Max(0,Math.Max(Min.Y-b.Max.Y,b.Min.Y-Max.Y));double z=Math.Max(0,Math.Max(Min.Z-b.Max.Z,b.Min.Z-Max.Z));return Math.Sqrt(x*x+y*y+z*z); }
}

internal struct Tri
{
    public Vec A,B,C;
    public Tri(Vec a,Vec b,Vec c) { A=a;B=b;C=c; }
    public Vec Normal => (B-A).Cross(C-A);
    public Box Bounds => new Box(new Vec(Math.Min(A.X,Math.Min(B.X,C.X)),Math.Min(A.Y,Math.Min(B.Y,C.Y)),Math.Min(A.Z,Math.Min(B.Z,C.Z))),new Vec(Math.Max(A.X,Math.Max(B.X,C.X)),Math.Max(A.Y,Math.Max(B.Y,C.Y)),Math.Max(A.Z,Math.Max(B.Z,C.Z))));
    public Vec Center => (A+B+C)*(1.0/3);
    public void Project(Vec n,out double min,out double max) { double a=A.Dot(n),b=B.Dot(n),c=C.Dot(n);min=Math.Min(a,Math.Min(b,c));max=Math.Max(a,Math.Max(b,c)); }
}

internal sealed class Tree
{
    public Box Bounds;
    public Tree Left,Right;
    public Tri[] Triangles;
    public Tree(Tri[] triangles)
    {
        if(triangles.Length==0)throw new ArgumentException("Empty mesh");
        Bounds=triangles[0].Bounds;foreach(var t in triangles.Skip(1))Bounds=Bounds.Union(t.Bounds);
        if(triangles.Length<=8){Triangles=triangles;return;}
        var s=Bounds.Size;int axis=s.X>=s.Y&&s.X>=s.Z?0:s.Y>=s.Z?1:2;
        var sorted=triangles.OrderBy(t=>t.Center.At(axis)).ToArray();int mid=sorted.Length/2;
        Left=new Tree(sorted.Take(mid).ToArray());Right=new Tree(sorted.Skip(mid).ToArray());
    }
}

internal sealed class Solid
{
    public readonly Tri[] Triangles;
    public readonly Tree Tree;
    public readonly Vec[] Vertices;
    public Solid(IEnumerable<Tri> source)
    {
        Triangles=source.Where(t=>t.Normal.Length>1e-15).ToArray();
        Tree=new Tree(Triangles);
        Vertices=Triangles.SelectMany(t=>new[]{t.A,t.B,t.C}).GroupBy(v=>v.Key).Select(g=>g.First()).ToArray();
    }
    public bool Inside(Vec p,double e)
    {
        if(!Tree.Bounds.Contains(p,e))return false;
        // Boundary contact is permitted. A vertex on a face is not penetration.
        foreach(var t in Nearby(Tree,p,e))if(OnTriangle(p,t,e))return false;
        var directions=new[]{new Vec(1,.317,.193).Unit,new Vec(.211,1,.419).Unit,new Vec(.337,.157,1).Unit};
        int votes=0;
        foreach(var direction in directions)
        {
            var hits=new List<double>();RayHits(Tree,p,direction,e,hits);
            hits.Sort();int count=0;double previous=double.NegativeInfinity;
            foreach(double hit in hits)if(hit-previous>e*4){count++;previous=hit;}
            if(count%2==1)votes++;
        }
        return votes>=2;
    }
    private static IEnumerable<Tri> Nearby(Tree n,Vec p,double e)
    {
        if(!n.Bounds.Contains(p,e))yield break;
        if(n.Triangles!=null){foreach(var t in n.Triangles)yield return t;yield break;}
        foreach(var t in Nearby(n.Left,p,e))yield return t;foreach(var t in Nearby(n.Right,p,e))yield return t;
    }
    private static bool OnTriangle(Vec p,Tri t,double e)
    {
        Vec n=t.Normal;double length=n.Length;if(length<1e-15||Math.Abs((p-t.A).Dot(n))/length>e)return false;
        Vec u=t.B-t.A,v=t.C-t.A,w=p-t.A;double uu=u.Dot(u),uv=u.Dot(v),vv=v.Dot(v),wu=w.Dot(u),wv=w.Dot(v),den=uu*vv-uv*uv;
        if(Math.Abs(den)<1e-24)return false;double b=(vv*wu-uv*wv)/den,c=(uu*wv-uv*wu)/den;
        return b>=-1e-9&&c>=-1e-9&&b+c<=1+1e-9;
    }
    private static bool RayBox(Box box,Vec o,Vec d,double e)
    {
        double lo=0,hi=double.PositiveInfinity;
        for(int i=0;i<3;i++){double v=d.At(i);if(Math.Abs(v)<1e-15){if(o.At(i)<box.Min.At(i)-e||o.At(i)>box.Max.At(i)+e)return false;continue;}double a=(box.Min.At(i)-o.At(i)-e)/v,b=(box.Max.At(i)-o.At(i)+e)/v;if(a>b){double k=a;a=b;b=k;}lo=Math.Max(lo,a);hi=Math.Min(hi,b);if(lo>hi)return false;}
        return hi>=0;
    }
    private static void RayHits(Tree tree,Vec o,Vec d,double e,List<double> hits)
    {
        if(!RayBox(tree.Bounds,o,d,e))return;
        if(tree.Triangles==null){RayHits(tree.Left,o,d,e,hits);RayHits(tree.Right,o,d,e,hits);return;}
        foreach(var t in tree.Triangles)
        {
            Vec edge1=t.B-t.A,edge2=t.C-t.A,h=d.Cross(edge2);double a=edge1.Dot(h);if(Math.Abs(a)<1e-14)continue;
            double inv=1/a;Vec s=o-t.A;double u=inv*s.Dot(h);if(u< -1e-10||u>1+1e-10)continue;Vec q=s.Cross(edge1);double v=inv*d.Dot(q);if(v< -1e-10||u+v>1+1e-10)continue;
            double distance=inv*edge2.Dot(q);if(distance>e)hits.Add(distance);
        }
    }
}

internal sealed class Body
{
    public string Id,Name;
    public int ManualOrder;
    public bool ForcedBase;
    public readonly List<Solid> Solids=new List<Solid>();
    public readonly List<Vec> Directions=new List<Vec>();
    public readonly HashSet<string> Predecessors=new HashSet<string>();
    public Box Bounds => Solids.Select(s=>s.Tree.Bounds).Aggregate((a,b)=>a.Union(b));
    public bool Valid => Solids.Count>0;
}

internal static class Collision
{
    // Continuous separating-axis intervals for two translating triangles.
    // No discrete path samples: a thin blocker cannot fall between samples.
    public static bool TriangleHit(Tri a,Tri b,Vec displacement,double start,double epsilon)
    {
        if(!a.Bounds.Sweep(displacement).Overlaps(b.Bounds,epsilon))return false;
        Vec[] ae={a.B-a.A,a.C-a.B,a.A-a.C},be={b.B-b.A,b.C-b.B,b.A-b.C};
        Vec an=a.Normal,bn=b.Normal;double lo=start,hi=1;
        if(!Axis(a,b,an,displacement,epsilon,ref lo,ref hi)||!Axis(a,b,bn,displacement,epsilon,ref lo,ref hi))return false;
        foreach(var x in ae)foreach(var y in be)if(!Axis(a,b,x.Cross(y),displacement,epsilon,ref lo,ref hi))return false;
        // Coplanar and parallel-face degeneracies require in-plane edge axes.
        foreach(var x in ae)if(!Axis(a,b,an.Cross(x),displacement,epsilon,ref lo,ref hi))return false;
        foreach(var x in be)if(!Axis(a,b,bn.Cross(x),displacement,epsilon,ref lo,ref hi))return false;
        return hi>=lo;
    }
    private static bool Axis(Tri a,Tri b,Vec axis,Vec move,double e,ref double lo,ref double hi)
    {
        double length=axis.Length;if(length<1e-13)return true;axis=axis*(1/length);
        a.Project(axis,out double amin,out double amax);b.Project(axis,out double bmin,out double bmax);double v=move.Dot(axis);
        if(Math.Abs(v)<1e-12)
        {
            // A persistent shared face/edge is contact, not a volume crossing.
            return amax>bmin+e && bmax>amin+e;
        }
        double first=(bmin-amax)/v,last=(bmax-amin)/v;if(first>last){double k=first;first=last;last=k;}
        lo=Math.Max(lo,first);hi=Math.Min(hi,last);return hi>=lo;
    }
    public static bool Swept(Body moving,Body obstacle,Vec displacement,double epsilon,Func<bool> cancel=null)
    {
        if(!moving.Bounds.Sweep(displacement).Overlaps(obstacle.Bounds,epsilon))return false;
        double start=Math.Max(1e-10,epsilon*2/Math.Max(displacement.Length,epsilon));
        foreach(var a in moving.Solids)foreach(var b in obstacle.Solids)
            if(Walk(a.Tree,b.Tree,displacement,start,epsilon,cancel))return true;
        return false;
    }
    public static bool InitialOverlap(Body a,Body b,double e,Func<bool> cancel=null)
    {
        if(!a.Bounds.InteriorOverlaps(b.Bounds,e))return false;
        foreach(var sa in a.Solids)foreach(var sb in b.Solids)
        {
            if(Walk(sa.Tree,sb.Tree,new Vec(),0,e,cancel))return true;
            foreach(var v in sa.Vertices){CheckCancellation(cancel);if(sb.Inside(v,e))return true;}
            foreach(var v in sb.Vertices){CheckCancellation(cancel);if(sa.Inside(v,e))return true;}
            foreach(var t in sa.Triangles){CheckCancellation(cancel);if(sb.Inside(t.Center,e))return true;}
            foreach(var t in sb.Triangles){CheckCancellation(cancel);if(sa.Inside(t.Center,e))return true;}
            // Coincident shells have no strictly interior vertices. Probe just
            // inside each face, in both orientations, to catch equal/flush solids.
            foreach(var t in sa.Triangles)
            {
                CheckCancellation(cancel);
                Vec n=t.Normal.Unit*(e*8);
                if(sa.Inside(t.Center+n,e)&&sb.Inside(t.Center+n,e))return true;
                if(sa.Inside(t.Center-n,e)&&sb.Inside(t.Center-n,e))return true;
            }
        }
        return false;
    }
    private static void CheckCancellation(Func<bool> cancel)
    {if(cancel!=null&&cancel())throw new OperationCanceledException();}
    private static bool Walk(Tree a,Tree b,Vec move,double start,double e,Func<bool> cancel)
    {
        if(cancel!=null&&cancel())throw new OperationCanceledException();
        if(!a.Bounds.Sweep(move).Overlaps(b.Bounds,e))return false;
        if(a.Triangles!=null&&b.Triangles!=null)
        {foreach(var ta in a.Triangles)foreach(var tb in b.Triangles)if(TriangleHit(ta,tb,move,start,e))return true;return false;}
        if(b.Triangles!=null||(a.Triangles==null&&a.Bounds.Size.Length>=b.Bounds.Size.Length))return Walk(a.Left,b,move,start,e,cancel)||Walk(a.Right,b,move,start,e,cancel);
        return Walk(a,b.Left,move,start,e,cancel)||Walk(a,b.Right,move,start,e,cancel);
    }
}
