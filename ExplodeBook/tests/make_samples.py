from pathlib import Path
import math
import rhino3dm as r
ROOT=Path(__file__).resolve().parents[1]/'samples'
ROOT.mkdir(exist_ok=True)
def mesh_cells(xs,ys,zs,occupied):
 m=r.Mesh();vertices={}
 def vertex(p):
  if p not in vertices:vertices[p]=m.Vertices.Add(*p)
  return vertices[p]
 faces=[((-1,0,0),(0,4,7,3)),((1,0,0),(1,2,6,5)),((0,-1,0),(0,1,5,4)),((0,1,0),(3,7,6,2)),((0,0,-1),(0,3,2,1)),((0,0,1),(4,5,6,7))]
 for i,j,k in sorted(occupied):
  x,X=xs[i:i+2];y,Y=ys[j:j+2];z,Z=zs[k:k+2]
  points=[(x,y,z),(X,y,z),(X,Y,z),(x,Y,z),(x,y,Z),(X,y,Z),(X,Y,Z),(x,Y,Z)]
  for (di,dj,dk),face in faces:
   if (i+di,j+dj,k+dk) not in occupied:m.Faces.AddFace(*[vertex(points[n]) for n in face])
 return m

def box(a,b):return r.Brep.CreateFromBox(r.Box(r.BoundingBox(r.Point3d(*a),r.Point3d(*b))))
def add(model,shape,name,base=False):
 attrs=r.ObjectAttributes();attrs.Name=name
 if base:attrs.SetUserString('ExplodeBook.ForcedBase','1')
 model.Objects.Add(shape,attrs)
def model():
 m=r.File3dm();m.Settings.ModelUnitSystem=r.UnitSystem.Millimeters;m.Settings.ModelAbsoluteTolerance=.001;return m
# One preassembled frame plus payload and separate lid. Payload must precede lid.
xyz=[0,1,4,5]
frame={(i,j,k) for i in range(3) for j in range(3) for k in range(3) if k==0 or i in (0,2) or j in (0,2)}
m=model();add(m,mesh_cells(xyz,xyz,xyz,frame),'预装机架',True);add(m,box((2,2,2),(3,3,3)),'内部零件');add(m,box((0,0,5),(5,5,6)),'最后安装盖板');m.Write(str(ROOT/'01_内部零件先于盖板.3dm'),7)
# Closed shell traps payload; must be rejected without invented steps.
shell={(i,j,k) for i in range(3) for j in range(3) for k in range(3) if i in (0,2) or j in (0,2) or k in (0,2)}
m=model();add(m,mesh_cells(xyz,xyz,xyz,shell),'封闭壳体',True);add(m,box((2,2,2),(3,3,3)),'受阻内部零件');m.Write(str(ROOT/'02_封闭结构应报告受阻.3dm'),7)
# Rotated through-slot; local normals are required to remove exact-fitting tab.
frame=mesh_cells([0,2],[0,4,6,10],[0,4,6,10],{(0,j,k) for j in range(3) for k in range(3) if (j,k)!=(1,1)})
tab=box((-2,4,4),(4,6,6));rotation=r.Transform.Rotation(math.pi/6,r.Vector3d(0,0,1),r.Point3d(0,0,0));frame.Transform(rotation);tab.Transform(rotation)
m=model();add(m,frame,'旋转30度带孔板',True);add(m,tab,'沿板面法线插入的插片');m.Write(str(ROOT/'03_旋转板件真实插槽.3dm'),7)
for p in ROOT.glob('*.3dm'):
 m=r.File3dm.Read(str(p));assert m and len(m.Objects)>0
 for obj in m.Objects:
  assert obj.Geometry.IsValid
  assert obj.Geometry.IsClosed if isinstance(obj.Geometry,r.Mesh) else obj.Geometry.IsSolid
 print(p.name,len(m.Objects),'objects',p.stat().st_size,'bytes')
