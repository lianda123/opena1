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
            var side = CurrentBaseMaterial(doc, first);
            var back = PreviousBackMaterial(doc, first);
            finish.SideReference = side == null ? null : new Material(side);
            finish.BackReference = back == null ? null : new Material(back);
            using (var previousTexture = back?.GetTexture(TextureType.Bitmap))
                finish.UseBoardMapping = finish.Sides.Kind == "texture" || finish.Back.Kind == "texture" ||
                    finish.Sides.Kind == "current" && first.Attributes.GetUserString(FinishChannelKey) != null ||
                    finish.Back.Kind == "current" && previousTexture != null;
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
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DielineUV", "textures");
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
            var path = Path.Combine(TextureCache(), "DielineUV-0413-" + name + ".png");
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

        private static int ChooseFinishChannel(RhinoObject obj)
        {
            for (int channel = 1; channel < 32; channel++)
                if (obj.Attributes.GetUserString(FinishChannelKey) == channel.ToString(CultureInfo.InvariantCulture) ||
                    obj.GetTextureMapping(channel) == null) return channel;
            throw new InvalidOperationException("零件没有空闲的板材贴图通道。");
        }

        private static bool PreparePrintingFaces(RhinoDoc doc, Change change, RhinoObject obj, Part part,
            Brep brep, int[] candidates, Mesh mappingMesh, Canvas canvas, SideFinish finish)
        {
            string mode = finish.Printing;
            Vector3d layoutFront;
            bool flat = TryReadLayoutFrontNormal(mappingMesh, canvas, out layoutFront);
            if (flat) candidates = FlatPrintFaces(brep, layoutFront) ?? candidates;
            change.AllPrintFaces = candidates;
            change.SingleSided = mode == "single" || mode == "reselect";
            change.PrintFaces = candidates;
            if (!change.SingleSided) return true;
            int[] selected;
            if (flat && finish.SingleSideSign.HasValue)
            {
                selected = candidates.Where(index => OrientedNormal(brep.Faces[index]) * layoutFront * finish.SingleSideSign.Value > 0.99).ToArray();
                if (selected.Length == 0)
                    throw new InvalidDataException("零件「" + part.Label + "」无法按第一件的刀模方向识别印刷侧，请检查绑定方向。");
            }
            else if (!flat)
            {
                // Fold templates already record their selected skin. This does not
                // introduce neutral-surface binding or infer a second folded skin.
                selected = candidates;
            }
            else selected = mode == "reselect" ? null : ReadFaceIndices(obj.Attributes.GetUserString(SingleFacesKey + part.MappingId));
            if (selected == null || selected.Length == 0 || selected.Any(index => !candidates.Contains(index)))
            {
                RhinoApp.WriteLine("只需为第一个平板确认印刷侧，后面的零件按各自已绑定的刀模方向自动沿用。");
                selected = PickPrintingFaces(doc, obj, brep, candidates, part.Label);
            }
            if (selected == null) return false;
            if (flat && !finish.SingleSideSign.HasValue)
            {
                double dot = OrientedNormal(brep.Faces[selected[0]]) * layoutFront;
                if (Math.Abs(dot) < 0.99) throw new InvalidDataException("首件印刷侧与刀模平面不平行，请重新绑定。");
                finish.SingleSideSign = dot >= 0 ? 1.0 : -1.0;
            }
            change.PrintFaces = selected;
            change.SingleFacesStorageKey = SingleFacesKey + part.MappingId;
            change.BackFaces = candidates.Except(selected).ToArray();
            if (change.BackFaces.Length == 0)
            {
                // Recorded folded skins contain only their printable side. The remaining
                // faces stay on the non-print finish; neutral-surface binding is separate.
                RhinoApp.WriteLine("零件「{0}」没有可独立识别的平板背面；其余表面使用侧面及非印刷面材质。", part.Label);
            }
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
                    picker.SetCommandPrompt("零件「" + label + "」：选择有图案的正面（Ctrl+Shift 点面；Mac 用 Command+Shift），回车确认");
                    picker.GeometryFilter = ObjectType.Surface;
                    picker.SubObjectSelect = true;
                    picker.GroupSelect = false;
                    picker.EnablePreSelect(false, true);
                    picker.SetCustomGeometryFilter((obj, geometry, component) => obj.Id == source.Id);
                    if (picker.GetMultiple(1, 0) != GetResult.Object) return null;
                    var chosen = new HashSet<int>();
                    for (int i = 0; i < picker.ObjectCount; i++)
                    {
                        using (var reference = picker.Object(i))
                        {
                            var face = reference.Face();
                            if (reference.ObjectId != source.Id || face == null) continue;
                            var bounds = face.GetBoundingBox(true);
                            // Extrusion cap component indices can differ from converted Brep indices.
                            foreach (int index in candidates)
                            {
                                var target = brep.Faces[index].GetBoundingBox(true);
                                if (bounds.Min.DistanceTo(target.Min) <= StoredGeometryTolerance &&
                                    bounds.Max.DistanceTo(target.Max) <= StoredGeometryTolerance) chosen.Add(index);
                            }
                        }
                    }
                    if (chosen.Count == 0 || chosen.Count == candidates.Length && candidates.Length > 1)
                    {
                        RhinoApp.WriteLine("请选择一侧的印刷大面，不选厚度侧面或两侧全部大面。");
                        continue;
                    }
                    // Opposite planar normals are different sides of a solid. Disallow
                    // accidentally selecting both sides while allowing split coplanar faces.
                    var normals = chosen.Select(index => OrientedNormal(brep.Faces[index])).ToArray();
                    bool planar = chosen.All(index => brep.Faces[index].IsPlanar(StoredGeometryTolerance));
                    if (planar && normals.Any(normal => normal * normals[0] < 0.99))
                    {
                        RhinoApp.WriteLine("选到了不同朝向的大面，请只选同一印刷侧。");
                        continue;
                    }
                    if (planar)
                        foreach (int index in candidates)
                            if (brep.Faces[index].IsPlanar(StoredGeometryTolerance) &&
                                OrientedNormal(brep.Faces[index]) * normals[0] > 0.99) chosen.Add(index);
                    doc.Objects.UnselectAll();
                    doc.Views.Redraw();
                    return chosen.OrderBy(index => index).ToArray();
                }
            }
        }

        private static Vector3d OrientedNormal(BrepFace face)
        {
            var normal = face.NormalAt(face.Domain(0).Mid, face.Domain(1).Mid);
            if (face.OrientationIsReversed) normal.Reverse();
            normal.Unitize();
            return normal;
        }

        private static Material CurrentBaseMaterial(RhinoDoc doc, RhinoObject obj)
        {
            int index = obj.Attributes.MaterialIndex;
            return obj.Attributes.MaterialSource == ObjectMaterialSource.MaterialFromObject && index >= 0 && index < doc.Materials.Count
                ? doc.Materials[index] : obj.GetMaterial(true);
        }

        private static Material CreateFinishMaterial(FinishSpec spec, Material current, int channel, string role)
        {
            // A copied material must not share the old renderer instance. That instance
            // can regenerate the old simulation and discard newly added face channels.
            var material = spec.Kind == "current" && current != null ? new Material(current) : new Material();
            material.Id = Guid.NewGuid();
            material.Name = "DielineUV " + role + " " + Guid.NewGuid().ToString("N").Substring(0, 8);
            material.RenderMaterialInstanceId = Guid.Empty;
            material.ClearMaterialChannels();
            if (spec.Kind == "color") material.DiffuseColor = spec.Color;
            else if (spec.Kind == "texture")
            {
                material.DiffuseColor = Color.White;
#pragma warning disable 612, 618
                using (var simulated = new Rhino.Render.SimulatedTexture { Filename = spec.Texture })
#pragma warning restore 612, 618
                {
                    simulated.SetMappingChannelAndProjectionMode(Rhino.Render.SimulatedTexture.ProjectionModes.MappingChannel,
                        channel, Rhino.Render.SimulatedTexture.EnvironmentMappingModes.Automatic);
                    using (var texture = simulated.Texture())
                        if (texture == null || !material.SetTexture(texture, TextureType.Bitmap))
                            throw new InvalidOperationException("无法建立板材图片材质。");
                }
            }
            else if (current == null) material.DiffuseColor = BrownEdge;
            if (spec.Kind == "current" && current?.Name?.StartsWith("DielineUV ", StringComparison.Ordinal) == true)
            {
                using (var old = current.GetTexture(TextureType.Bitmap))
                    if (old != null && old.MappingChannelId > 0 && old.MappingChannelId < 32)
                    {
#pragma warning disable 612, 618
                        using (var simulated = new SimulatedTexture { Filename = old.FileName })
#pragma warning restore 612, 618
                        {
                            simulated.SetMappingChannelAndProjectionMode(SimulatedTexture.ProjectionModes.MappingChannel,
                                channel, SimulatedTexture.EnvironmentMappingModes.Automatic);
                            using (var texture = simulated.Texture())
                                if (texture == null || !material.SetTexture(texture, TextureType.Bitmap))
                                    throw new InvalidOperationException("无法沿用整个刀模框的板材图片。");
                        }
                    }
            }
            return material;
        }

        private static Material PreviousBackMaterial(RhinoDoc doc, RhinoObject obj)
        {
            Guid id;
            if (!Guid.TryParse(obj.Attributes.GetUserString(BackMaterialKey), out id)) return null;
            int index = doc.Materials.Find(id, true);
            return index < 0 ? null : doc.Materials[index];
        }

        private static TextureMapping BoardTextureMapping(GeometryBase geometry)
        {
            var box = geometry.GetBoundingBox(true);
            return TextureMapping.CreateBoxMapping(Plane.WorldXY,
                ExpandedInterval(box.Min.X, box.Max.X), ExpandedInterval(box.Min.Y, box.Max.Y),
                ExpandedInterval(box.Min.Z, box.Max.Z), true);
        }

        private static Interval ExpandedInterval(double min, double max)
        {
            return max - min > StoredGeometryTolerance ? new Interval(min, max) : new Interval(min - 0.5, max + 0.5);
        }

        private static string ExistingArtworkImage(RhinoDoc doc, Bundle bundle)
        {
            foreach (var part in bundle.Parts)
            {
                var obj = doc.Objects.FindId(Guid.Parse(part.SourceId));
                if (obj == null) continue;
                string saved = obj.Attributes.GetUserString(ArtworkImageKey);
                if (File.Exists(saved)) return saved;
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
