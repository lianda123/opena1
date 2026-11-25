using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.Render;
using Rhino.UI;

namespace DielineUV
{
    public sealed partial class DielineUVCommand
    {
        private static SideFinish ChooseSideFinish()
        {
            var modes = new[] { "沿用此刀模框上次设置（以第一件为准）", "整个刀模框：双面同图", "整个刀模框：单面印刷", "整个刀模框：单面印刷并重选第一件印刷侧", "整件贴图（兼容原效果）" };
            var mode = Dialogs.ShowListBox(Title, "本次设置用于整个刀模框，只需确认一次", modes) as string;
            if (mode == null) return null;
            if (mode == modes[4]) return new SideFinish { WholeObject = true, Printing = "whole" };
            var sides = ChooseFinishSpec("选择侧面及其他非印刷面的材质", false);
            if (sides == null) return null;
            FinishSpec back = mode == modes[1] ? new FinishSpec() :
                ChooseFinishSpec("整个刀模框的统一背面材质（仅单面印刷时生效）", true);
            if (back == null) return null;
            return new SideFinish { Printing = mode == modes[1] ? "double" : mode == modes[2] ? "single" :
                mode == modes[3] ? "reselect" : "keep", Sides = sides, Back = back };
        }

        private static void PrepareProjectFinish(RhinoDoc doc, Bundle bundle, SideFinish finish)
        {
            var first = doc.Objects.FindId(Guid.Parse(bundle.Parts[0].SourceId));
            if (first == null) throw new InvalidOperationException("找不到此刀模框的第一个原件，请确认对应模型。");
            if (finish.Printing == "keep")
            {
                string previous = first.Attributes.GetUserString(PrintingModeKey);
                finish.WholeObject = previous == "whole";
                finish.Printing = finish.WholeObject ? "whole" : previous == "single" ? "single" : "double";
            }
            if (finish.WholeObject) return;
            finish.Sides = ResolveFinish(finish.Sides, first.Attributes.GetUserString(SideSpecKey), CurrentBaseMaterial(doc, first));
            var previousBack = PreviousBackMaterial(doc, first);
            if (finish.Back.Kind == "same" || finish.Back.Kind == "current" &&
                first.Attributes.GetUserString(BackSpecKey) == null && previousBack == null)
                finish.Back = finish.Sides;
            else finish.Back = ResolveFinish(finish.Back, first.Attributes.GetUserString(BackSpecKey), previousBack);
        }

        private static FinishSpec ResolveFinish(FinishSpec spec, string saved, Material current)
        {
            if (spec.Kind != "current") return spec;
            var decoded = DecodeFinishSpec(saved);
            if (decoded != null) return decoded;
            using (var bitmap = current?.GetTexture(TextureType.Bitmap))
            {
                if (bitmap != null && File.Exists(bitmap.FileName))
                {
                    if (bitmap.MappingChannelId >= 32 && current.Name?.StartsWith("DielineUV ", StringComparison.Ordinal) == true)
                        return new FinishSpec { Kind = "color", Color = BrownEdge };
                    return new FinishSpec { Kind = "texture", Texture = CacheTexture(bitmap.FileName) };
                }
            }
            return new FinishSpec { Kind = "color", Color = current?.DiffuseColor ?? BrownEdge };
        }

        private static string EncodeFinishSpec(FinishSpec spec)
        {
            return spec.Kind == "texture" ? "t:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(spec.Texture)) :
                "c:" + spec.Color.R.ToString("X2") + spec.Color.G.ToString("X2") + spec.Color.B.ToString("X2");
        }

        private static FinishSpec DecodeFinishSpec(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (text.StartsWith("c:", StringComparison.Ordinal))
            {
                int value;
                if (int.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                    return new FinishSpec { Kind = "color", Color = Color.FromArgb((value >> 16) & 255, (value >> 8) & 255, value & 255) };
            }
            if (text.StartsWith("t:", StringComparison.Ordinal))
            {
                var path = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(text.Substring(2)));
                if (!File.Exists(path)) throw new FileNotFoundException("找不到上次选择的板材图片，请重新选择板材材质。", path);
                return new FinishSpec { Kind = "texture", Texture = path };
            }
            return null;
        }

        private static FinishSpec ChooseFinishSpec(string prompt, bool back)
        {
            var options = new List<string> { back ? "沿用首件背面材质（首次与侧面相同）" : "沿用首件当前材质（整框统一）", "棕色", "自定义颜色", "密度板原色（图片材质）", "木板原色（图片材质）", "选择其他材质图片" };
            if (back) options.Add("与侧面材质相同");
            var choice = Dialogs.ShowListBox(Title, prompt, options) as string;
            if (choice == null) return null;
            int index = options.IndexOf(choice);
            if (index == 0) return new FinishSpec();
            if (index == 1) return new FinishSpec { Kind = "color", Color = BrownEdge };
            if (index == 2)
            {
                var color = BrownEdge;
                return Dialogs.ShowColorDialog(ref color) ? new FinishSpec { Kind = "color",
                    Color = Color.FromArgb(color.R, color.G, color.B) } : null;
            }
            if (index == 3 || index == 4)
                return new FinishSpec { Kind = "texture", Texture = BuiltInTexture(index == 3 ? "MDF" : "Wood") };
            if (index == 5)
            {
                using (var dialog = new Eto.Forms.OpenFileDialog { Title = "选择背面或侧面材质图片" })
                {
                    dialog.Filters.Add(new Eto.Forms.FileFilter("材质图片", ".png", ".jpg", ".jpeg"));
                    return dialog.ShowDialog(RhinoEtoApp.MainWindow) == Eto.Forms.DialogResult.Ok
                        ? new FinishSpec { Kind = "texture", Texture = CacheTexture(dialog.FileName) } : null;
                }
            }
            return new FinishSpec { Kind = "same" };
        }

        private static string TextureCache()
        {
            var path = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "DielineUV", "textures");
            Directory.CreateDirectory(path);
            return path;
        }

        private static string CacheTexture(string source)
        {
            var target = Path.Combine(TextureCache(), Guid.NewGuid().ToString("N") + Path.GetExtension(source));
            File.Copy(source, target);
            return target;
        }

        private static string BuiltInTexture(string name)
        {
            // Embed the supplied images so a single RHP is sufficient on both platforms.
            var path = Path.Combine(TextureCache(), "DielineUV-0414-" + name + ".png");
            if (!File.Exists(path))
            {
                using (var resource = typeof(DielineUVCommand).Assembly.GetManifestResourceStream("DielineUV.Textures." + name + ".png"))
                {
                    if (resource == null) throw new InvalidDataException("安装包缺少板材图片 " + name + "。");
                    using (var file = File.Create(path)) resource.CopyTo(file);
                }
            }
            return path;
        }



        private static bool PreparePrintingFaces(RhinoDoc doc, Change change, RhinoObject obj, Part part,
            Brep brep, int[] candidates, Mesh mappingMesh, Canvas canvas, SideFinish finish)
        {
            Vector3d layoutFront;
            bool flat = TryReadLayoutFrontNormal(mappingMesh, canvas, out layoutFront);
            if (flat) candidates = FlatPrintFaces(brep, layoutFront) ?? candidates;
            change.AllPrintFaces = candidates;
            change.SingleSided = finish.Printing == "single" || finish.Printing == "reselect";
            change.PrintFaces = candidates;
            if (!change.SingleSided) return true;
            if (!flat)
            {
                // Existing folded templates retain the skin they recorded.
                change.BackFaces = new int[0];
                return true;
            }
            var positions = candidates.Select(index => {
                var face = brep.Faces[index];
                var point = face.PointAt(face.Domain(0).Mid, face.Domain(1).Mid);
                return point.X * layoutFront.X + point.Y * layoutFront.Y + point.Z * layoutFront.Z;
            }).ToArray();
            int[] front, back;
            if (!PanelSidePolicy.Split(positions, finish.SingleSideSign ?? 1, StoredGeometryTolerance, out front, out back))
                throw new InvalidDataException("零件「" + part.Label + "」没有可分离的板厚，请使用带厚度的平板实体。");
            if (!finish.SingleSideSign.HasValue)
            {
                var saved = finish.Printing == "reselect" ? null :
                    ReadFaceIndices(obj.Attributes.GetUserString(SingleFacesKey + part.MappingId));
                var selected = saved != null && saved.Length > 0 && saved.All(candidates.Contains) ? saved :
                    PickPrintingFaces(doc, obj, brep, candidates, part.Label);
                if (selected == null) return false;
                double firstPosition = positions[Array.IndexOf(candidates, selected[0])];
                finish.SingleSideSign = firstPosition > positions.Min() + (positions.Max() - positions.Min()) / 2 ? 1 : -1;
                PanelSidePolicy.Split(positions, finish.SingleSideSign.Value, StoredGeometryTolerance, out front, out back);
                RhinoApp.WriteLine("已确认首件印刷侧，其余零件按各自刀模方向及板厚位置自动沿用。");
            }
            change.PrintFaces = front.Select(index => candidates[index]).ToArray();
            change.BackFaces = back.Select(index => candidates[index]).ToArray();
            change.SingleFacesStorageKey = SingleFacesKey + part.MappingId;
            return true;
        }

        private static bool TryReadLayoutFrontNormal(Mesh mesh, Canvas canvas, out Vector3d normal)
        {
            if (!TryReadProjectionNormal(mesh, canvas, out normal)) return false;
            double largest = 0;
            foreach (var face in mesh.Faces)
            {
                var a = mesh.TextureCoordinates[face.A];
                var b = mesh.TextureCoordinates[face.B];
                var c = mesh.TextureCoordinates[face.C];
                double signed = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                if (Math.Abs(signed) <= largest) continue;
                largest = Math.Abs(signed);
                var ab = mesh.Vertices.Point3dAt(face.B) - mesh.Vertices.Point3dAt(face.A);
                var ac = mesh.Vertices.Point3dAt(face.C) - mesh.Vertices.Point3dAt(face.A);
                normal = Vector3d.CrossProduct(ab, ac);
                if (signed < 0) normal.Reverse();
            }
            return largest > 1e-12 && normal.Unitize();
        }

        private static int[] PickPrintingFaces(RhinoDoc doc, RhinoObject source, Brep brep, int[] candidates, string label)
        {
            while (true)
            {
                using (var picker = new GetObject())
                {
                    picker.SetCommandPrompt("首件「" + label + "」：点选要印刷的一侧大面（Ctrl+Shift；Mac 用 Command+Shift）");
                    picker.GeometryFilter = ObjectType.Surface;
                    picker.SubObjectSelect = true;
                    picker.GroupSelect = false;
                    picker.EnablePreSelect(false, true);
                    picker.SetCustomGeometryFilter((obj, geometry, component) => obj.Id == source.Id);
                    if (picker.Get() != GetResult.Object) return null;
                    using (var reference = picker.Object(0))
                    {
                        var face = reference.Face();
                        if (face == null) continue;
                        if (source.Geometry is Brep && candidates.Contains(face.FaceIndex))
                            return new[] { face.FaceIndex };
                        // Match converted extrusion caps geometrically without using their orientation flags.
                        using (var selected = face.DuplicateFace(false))
                        {
                            var bounds = selected.GetBoundingBox(true);
                            foreach (int index in candidates)
                                using (var test = brep.Faces[index].DuplicateFace(false))
                                {
                                    var target = test.GetBoundingBox(true);
                                    if (bounds.Min.DistanceTo(target.Min) <= StoredGeometryTolerance &&
                                        bounds.Max.DistanceTo(target.Max) <= StoredGeometryTolerance) return new[] { index };
                                }
                        }
                    }
                    RhinoApp.WriteLine("请选择该原件的一侧大面，不选厚度侧边。");
                }
            }
        }

        private static Material CurrentBaseMaterial(RhinoDoc doc, RhinoObject obj)
        {
            int index = obj.Attributes.MaterialIndex;
            return obj.Attributes.MaterialSource == ObjectMaterialSource.MaterialFromObject && index >= 0 && index < doc.Materials.Count
                ? doc.Materials[index] : obj.GetMaterial(true);
        }



        private static Material PreviousBackMaterial(RhinoDoc doc, RhinoObject obj)
        {
            Guid id;
            if (!Guid.TryParse(obj.Attributes.GetUserString(BackMaterialKey), out id)) return null;
            int index = doc.Materials.Find(id, true);
            return index < 0 ? null : doc.Materials[index];
        }



        private static string ExistingArtworkImage(RhinoDoc doc, Bundle bundle)
        {
            foreach (var part in bundle.Parts)
            {
                var obj = doc.Objects.FindId(Guid.Parse(part.SourceId));
                if (obj == null) continue;
                string saved = obj.Attributes.GetUserString(ArtworkImageKey);
                if (File.Exists(saved)) return saved;
                if (obj.Attributes.GetUserString(AtlasImageKey) != null) continue;
                // Upgrade objects from 0.4.12 which did not save the PNG path.
                var material = CurrentBaseMaterial(doc, obj);
                var geometry = obj.Geometry as Brep;
                var printFaces = ReadFaceIndices(obj.Attributes.GetUserString(SingleFacesKey + part.MappingId)) ??
                    part.PrintFaces ?? ReadFaceIndices(obj.Attributes.GetUserString(PrintFacesKey + "." + part.MappingId));
                if (material != null && geometry != null)
                    foreach (var face in geometry.Faces)
                    {
                        if (printFaces != null && !printFaces.Contains(face.FaceIndex)) continue;
                        var id = material.MaterialChannelIdFromIndex(face.MaterialChannelIndex);
                        int index = id == Guid.Empty ? -1 : doc.Materials.Find(id, true);
                        if (index < 0) continue;
                        using (var texture = doc.Materials[index].GetTexture(TextureType.Bitmap))
                            if (texture != null && texture.MappingChannelId >= 32 && File.Exists(texture.FileName)) return texture.FileName;
                    }
                using (var texture = material?.GetTexture(TextureType.Bitmap))
                    if (texture != null && texture.MappingChannelId >= 32 && File.Exists(texture.FileName)) return texture.FileName;
            }
            return null;
        }
    }
}
