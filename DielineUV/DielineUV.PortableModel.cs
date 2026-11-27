using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DielineUV
{
    internal sealed class PortablePart
    {
        public string Name, SourceId;
        public double[] Positions, Normals, Uv;
        public int[] Triangles;
        public int Material;
    }

    internal sealed class PortableMaterial
    {
        public string Name, ColorPath, OpacityPath;
        public byte[] Image;
        public bool Alpha;
    }

    // Standard mesh coordinates and per-vertex UVs only. No Rhino mapping channels.
    internal static class PortableModel
    {
        internal static void Validate(IList<PortablePart> parts, IList<PortableMaterial> materials, double meters)
        {
            if (parts == null || parts.Count == 0 || materials == null || materials.Count == 0 || !Finite(meters) || meters <= 0)
                throw new InvalidDataException("缺少模型、材质或有效单位。");
            foreach (var p in parts)
            {
                if (p.Positions == null || p.Positions.Length == 0 || p.Positions.Length % 3 != 0 ||
                    p.Normals == null || p.Normals.Length != p.Positions.Length || p.Uv == null ||
                    p.Uv.Length != p.Positions.Length / 3 * 2 || p.Triangles == null || p.Triangles.Length == 0 ||
                    p.Triangles.Length % 3 != 0 || p.Material < 0 || p.Material >= materials.Count)
                    throw new InvalidDataException("零件的坐标、法线或 UV 不完整：" + p.Name);
                if (p.Positions.Any(v => !Finite(v)) || p.Normals.Any(v => !Finite(v)) || p.Uv.Any(v => !Finite(v)) ||
                    p.Triangles.Any(i => i < 0 || i >= p.Positions.Length / 3))
                    throw new InvalidDataException("零件包含无效坐标或索引：" + p.Name);
            }
            foreach (var material in materials)
                if (material.Image == null || material.Image.Length < 8 || material.Image[0] != 137 || material.Image[1] != 80 ||
                    !SafeRelative(material.ColorPath) || material.OpacityPath != null && !SafeRelative(material.OpacityPath))
                    throw new InvalidDataException("缺少 PNG 材质或材质路径不是相对路径。");
        }

        internal static bool Finite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }
        internal static int TexturePixel(int color, int? opacityMask, bool useSourceAlpha)
        {
            int alpha = useSourceAlpha ? (int)((uint)color >> 24) : 255;
            if (opacityMask.HasValue)
            {
                int m = opacityMask.Value;
                alpha = (int)Math.Round(alpha * (((m >> 16) & 255) + ((m >> 8) & 255) + (m & 255)) / (3.0 * 255));
            }
            return (alpha << 24) | (color & 0x00ffffff);
        }
        private static bool SafeRelative(string path)
        {
            return !string.IsNullOrEmpty(path) && !path.StartsWith("/") && !path.Contains(":") && !path.Contains("\\") &&
                !path.Split('/').Any(p => p == ".." || p == "." || p.Length == 0 || p.Any(char.IsWhiteSpace));
        }
        private static string N(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }

        internal static void WriteObj(string folder, IList<PortablePart> parts, IList<PortableMaterial> materials, double meters)
        {
            Validate(parts, materials, meters);
            using (var obj = new StreamWriter(Path.Combine(folder, "model.obj"), false, new UTF8Encoding(false)))
            {
                obj.WriteLine("# DielineUV: baked UVs; Y up; positions in meters; OBJ UV origin is bottom-left.");
                obj.WriteLine("mtllib model.mtl");
                int offset = 1;
                for (int pi = 0; pi < parts.Count; pi++)
                {
                    var p = parts[pi];
                    obj.WriteLine("o part_" + pi.ToString("D4", CultureInfo.InvariantCulture) + "_" + Token(p.SourceId));
                    obj.WriteLine("usemtl material_" + p.Material.ToString("D4", CultureInfo.InvariantCulture));
                    for (int i = 0; i < p.Positions.Length; i += 3)
                        obj.WriteLine("v " + N(p.Positions[i] * meters) + " " + N(p.Positions[i + 2] * meters) + " " + N(-p.Positions[i + 1] * meters));
                    for (int i = 0; i < p.Uv.Length; i += 2) obj.WriteLine("vt " + N(p.Uv[i]) + " " + N(p.Uv[i + 1]));
                    for (int i = 0; i < p.Normals.Length; i += 3)
                        obj.WriteLine("vn " + N(p.Normals[i]) + " " + N(p.Normals[i + 2]) + " " + N(-p.Normals[i + 1]));
                    for (int i = 0; i < p.Triangles.Length; i += 3)
                    {
                        obj.Write("f");
                        for (int j = 0; j < 3; j++) { string k = (offset + p.Triangles[i + j]).ToString(CultureInfo.InvariantCulture); obj.Write(" " + k + "/" + k + "/" + k); }
                        obj.WriteLine();
                    }
                    offset = checked(offset + p.Positions.Length / 3);
                }
            }
            using (var mtl = new StreamWriter(Path.Combine(folder, "model.mtl"), false, new UTF8Encoding(false)))
                for (int i = 0; i < materials.Count; i++)
                {
                    var m = materials[i];
                    mtl.WriteLine("newmtl material_" + i.ToString("D4", CultureInfo.InvariantCulture));
                    mtl.WriteLine("Ka 0 0 0\nKd 1 1 1\nKs 0 0 0\nNs 0\nd 1\nillum 2");
                    mtl.WriteLine("map_Kd " + m.ColorPath);
                    if (m.OpacityPath != null) mtl.WriteLine("map_d " + m.OpacityPath);
                    mtl.WriteLine();
                }
        }

        private static string Token(string id)
        {
            return new string((id ?? "part").Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());
        }

        internal static void WriteGlb(Stream output, IList<PortablePart> parts, IList<PortableMaterial> materials, double meters)
        {
            Validate(parts, materials, meters);
            var views = new List<object>(); var accessors = new List<object>();
            var meshes = new List<object>(); var nodes = new List<object>(); var images = new List<object>();
            using (var binary = new MemoryStream())
            using (var writer = new BinaryWriter(binary, Encoding.UTF8, true))
            {
                foreach (var p in parts)
                {
                    int count = p.Positions.Length / 3;
                    var min = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
                    var max = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
                    int start = (int)binary.Position;
                    for (int i = 0; i < p.Positions.Length; i += 3)
                    {
                        var v = new[] { (float)(p.Positions[i] * meters), (float)(p.Positions[i + 2] * meters), (float)(-p.Positions[i + 1] * meters) };
                        for (int j = 0; j < 3; j++) { if (float.IsInfinity(v[j])) throw new InvalidDataException("模型坐标超出 GLB 范围。"); writer.Write(v[j]); min[j] = Math.Min(min[j], v[j]); max[j] = Math.Max(max[j], v[j]); }
                    }
                    int pos = Accessor(views, accessors, start, (int)binary.Position - start, count, "VEC3", 5126, 34962, min, max);
                    start = (int)binary.Position;
                    for (int i = 0; i < p.Normals.Length; i += 3) { writer.Write((float)p.Normals[i]); writer.Write((float)p.Normals[i + 2]); writer.Write((float)-p.Normals[i + 1]); }
                    int normal = Accessor(views, accessors, start, (int)binary.Position - start, count, "VEC3", 5126, 34962, null, null);
                    start = (int)binary.Position;
                    for (int i = 0; i < p.Uv.Length; i += 2)
                    {
                        writer.Write((float)p.Uv[i]); writer.Write((float)(1 - p.Uv[i + 1])); // glTF origin is top-left; PNG bytes stay unchanged.
                    }
                    int uv = Accessor(views, accessors, start, (int)binary.Position - start, count, "VEC2", 5126, 34962, null, null);
                    start = (int)binary.Position;
                    foreach (int index in p.Triangles) writer.Write((uint)index);
                    int indices = Accessor(views, accessors, start, (int)binary.Position - start, p.Triangles.Length, "SCALAR", 5125, 34963, null, null);
                    meshes.Add(O("name", p.Name, "primitives", new[] { O("attributes", O("POSITION", pos, "NORMAL", normal, "TEXCOORD_0", uv), "indices", indices, "material", p.Material, "mode", 4) }));
                    nodes.Add(O("name", p.Name, "mesh", meshes.Count - 1, "extras", O("source_id", p.SourceId)));
                }
                foreach (var material in materials)
                {
                    Align(writer, 0); int start = (int)binary.Position; writer.Write(material.Image);
                    views.Add(O("buffer", 0, "byteOffset", start, "byteLength", material.Image.Length));
                    images.Add(O("bufferView", views.Count - 1, "mimeType", "image/png", "name", material.Name));
                }
                int dataLength = (int)binary.Length;
                var gltfMaterials = materials.Select((m, i) => O("name", m.Name,
                    "pbrMetallicRoughness", O("baseColorFactor", new[] { 1, 1, 1, 1 }, "baseColorTexture", O("index", i, "texCoord", 0), "metallicFactor", 0, "roughnessFactor", .85),
                    "alphaMode", m.Alpha ? "BLEND" : "OPAQUE", "doubleSided", true)).ToArray();
                var document = O("asset", O("version", "2.0", "generator", "DielineUV 0.4.19"), "scene", 0,
                    "scenes", new[] { O("nodes", Enumerable.Range(0, parts.Count).ToArray()) }, "nodes", nodes, "meshes", meshes,
                    "materials", gltfMaterials, "images", images,
                    "textures", materials.Select((m, i) => O("sampler", 0, "source", i)).ToArray(),
                    "samplers", new[] { O("magFilter", 9729, "minFilter", 9987, "wrapS", 10497, "wrapT", 10497) },
                    "buffers", new[] { O("byteLength", dataLength) }, "bufferViews", views, "accessors", accessors);
                byte[] json = Encoding.UTF8.GetBytes(Json(document));
                int jsonLength = checked((json.Length + 3) & ~3), binLength = checked((dataLength + 3) & ~3);
                using (var glb = new BinaryWriter(output, Encoding.UTF8, true))
                {
                    glb.Write(0x46546c67u); glb.Write(2u); glb.Write(checked((uint)(12 + 8 + jsonLength + 8 + binLength)));
                    glb.Write((uint)jsonLength); glb.Write(0x4e4f534au); glb.Write(json);
                    for (int i = json.Length; i < jsonLength; i++) glb.Write((byte)32);
                    glb.Write((uint)binLength); glb.Write(0x004e4942u); glb.Write(binary.ToArray());
                    for (int i = dataLength; i < binLength; i++) glb.Write((byte)0);
                }
            }
        }

        private static int Accessor(List<object> views, List<object> accessors, int start, int length, int count, string type, int component, int target, double[] min, double[] max)
        {
            views.Add(O("buffer", 0, "byteOffset", start, "byteLength", length, "target", target));
            var a = O("bufferView", views.Count - 1, "byteOffset", 0, "componentType", component, "count", count, "type", type);
            if (min != null) { a["min"] = min; a["max"] = max; }
            accessors.Add(a); return accessors.Count - 1;
        }
        private static void Align(BinaryWriter writer, byte padding) { while ((writer.BaseStream.Position & 3) != 0) writer.Write(padding); }
        internal static Dictionary<string, object> O(params object[] values)
        {
            var d = new Dictionary<string, object>(); for (int i = 0; i < values.Length; i += 2) d.Add((string)values[i], values[i + 1]); return d;
        }
        internal static string Json(object value)
        {
            if (value == null) return "null";
            var s = value as string;
            if (s != null)
            {
                var b = new StringBuilder("\"");
                foreach (char c in s)
                    if (c == '"' || c == '\\') b.Append('\\').Append(c);
                    else if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else b.Append(c);
                return b.Append('"').ToString();
            }
            if (value is bool) return (bool)value ? "true" : "false";
            var dictionary = value as IDictionary<string, object>;
            if (dictionary != null) return "{" + string.Join(",", dictionary.Select(p => Json(p.Key) + ":" + Json(p.Value))) + "}";
            var array = value as IEnumerable;
            if (array != null) { var items = new List<string>(); foreach (var item in array) items.Add(Json(item)); return "[" + string.Join(",", items) + "]"; }
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }
}

