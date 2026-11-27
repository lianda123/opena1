using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using DielineUV;

internal static class PortableExportTests
{
    private static int checks;
    private static void Check(bool condition, string text) { checks++; if (!condition) throw new Exception(text); }
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGP4z8DwHwyBNBAwJAAARysIWQeWF/YAAAAASUVORK5CYII=");

    internal static void Run()
    {
        var part = new PortablePart { Name = "边界\"测试\n零件", SourceId = "source-first", Material = 0,
            Positions = new double[] { 0,0,0, 100,0,0, 0,100,0, 0,0,0, 0,100,0, -100,0,0 },
            Normals = Enumerable.Range(0,6).SelectMany(i => new double[] { 0,0,1 }).ToArray(),
            Uv = new double[] { .125,.25, .5,.25, .125,.75, .875,.875, .75,.875, .875,.5 },
            Triangles = new[] { 0,1,2, 3,4,5 } };
        var second = new PortablePart { Name = "second part", SourceId = "source-second", Material = 1,
            Positions = new double[] { 1000,2000,3000, 1100,2000,3000, 1000,2100,3000 },
            Normals = new double[] { 0,0,1,0,0,1,0,0,1 }, Uv = new double[] { 0,0,1,0,0,1 }, Triangles = new[] { 0,1,2 } };
        var materials = new[] {
            new PortableMaterial { Name = "opaque", ColorPath = "textures/first.png", Image = Png },
            new PortableMaterial { Name = "transparent", ColorPath = "textures/second.png", OpacityPath = "textures/second_opacity.png", Image = Png, Alpha = true }
        };
        var parts = new[] { part, second };
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            using (var file = new MemoryStream())
            {
                PortableModel.WriteGlb(file, parts, materials, .001);
                var bytes = file.ToArray(); var reader = new BinaryReader(new MemoryStream(bytes));
                Check(reader.ReadUInt32() == 0x46546c67, "GLB magic"); Check(reader.ReadUInt32() == 2, "GLB version");
                Check(reader.ReadUInt32() == bytes.Length, "GLB total length");
                int jsonLength = reader.ReadInt32(); Check(jsonLength % 4 == 0, "JSON alignment");
                Check(reader.ReadUInt32() == 0x4e4f534a, "JSON chunk type");
                using (var json = JsonDocument.Parse(Encoding.UTF8.GetString(reader.ReadBytes(jsonLength))))
                {
                    int binLength = reader.ReadInt32(); Check(reader.ReadUInt32() == 0x004e4942, "BIN chunk type");
                    var bin = reader.ReadBytes(binLength); var root = json.RootElement;
                    Check(reader.BaseStream.Position == bytes.Length, "trailing bytes");
                    Check(root.GetProperty("buffers")[0].GetProperty("byteLength").GetInt32() <= bin.Length, "buffer bounds");
                    Check(root.GetProperty("nodes").GetArrayLength() == 2, "separate part nodes");
                    Check(root.GetProperty("nodes")[0].GetProperty("extras").GetProperty("source_id").GetString() == part.SourceId, "source identity");
                    Check(root.GetProperty("nodes")[0].GetProperty("name").GetString() == part.Name, "escaped Unicode name");
                    var primitive = root.GetProperty("meshes")[0].GetProperty("primitives")[0];
                    float[] position = Floats(root, bin, primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32(), 3);
                    float[] uv = Floats(root, bin, primitive.GetProperty("attributes").GetProperty("TEXCOORD_0").GetInt32(), 2);
                    Check(position.Length == 18, "UV seam vertices were merged");
                    Check(Math.Abs(position[3] - .1f) < 1e-7, "millimeters were not converted to meters");
                    Check(Math.Abs(position[8] + .1f) < 1e-7 && position[7] == 0, "Rhino Z-up to glTF Y-up basis");
                    Check(position[0] == position[9] && position[1] == position[10] && position[2] == position[11], "seam positions changed");
                    Check(uv[0] != uv[6], "coincident vertices lost distinct UVs");
                    for (int i = 0; i < part.Uv.Length; i += 2)
                    {
                        Check(Math.Abs(uv[i] - part.Uv[i]) < 1e-7, "U coordinate changed");
                        Check(Math.Abs(uv[i + 1] - (1 - part.Uv[i + 1])) < 1e-7, "V was not converted exactly once");
                    }
                    foreach (var v in root.GetProperty("bufferViews").EnumerateArray())
                    {
                        int offset = v.GetProperty("byteOffset").GetInt32(), length = v.GetProperty("byteLength").GetInt32();
                        Check(offset % 4 == 0, "buffer view alignment"); Check(offset >= 0 && offset + length <= bin.Length, "buffer view bounds");
                    }
                    foreach (var image in root.GetProperty("images").EnumerateArray())
                    {
                        Check(!image.TryGetProperty("uri", out _), "GLB retained external image path");
                        var view = root.GetProperty("bufferViews")[image.GetProperty("bufferView").GetInt32()];
                        var data = bin.Skip(view.GetProperty("byteOffset").GetInt32()).Take(view.GetProperty("byteLength").GetInt32()).ToArray();
                        Check(data.SequenceEqual(Png), "embedded PNG changed");
                    }
                    Check(root.GetProperty("materials")[0].GetProperty("alphaMode").GetString() == "OPAQUE", "opaque material mode");
                    Check(root.GetProperty("materials")[1].GetProperty("alphaMode").GetString() == "BLEND", "transparent material mode");
                    foreach (var m in root.GetProperty("materials").EnumerateArray())
                        Check(m.GetProperty("pbrMetallicRoughness").GetProperty("baseColorTexture").GetProperty("texCoord").GetInt32() == 0, "nonportable mapping channel");
                }
                // CI validates this same produced GLB with the independent Khronos validator.
                var fixture = Path.Combine("DielineUV", "Tests", "artifacts", "portable-fixture.glb");
                if (Directory.Exists(Path.Combine("DielineUV", "Tests")))
                { Directory.CreateDirectory(Path.GetDirectoryName(fixture)); File.WriteAllBytes(fixture, bytes); }
            }
            string token = "DielineUV-portable-" + Guid.NewGuid().ToString("N"), folder = Path.Combine(Path.GetTempPath(), token);
            Directory.CreateDirectory(folder);
            try
            {
                PortableModel.WriteObj(folder, parts, materials, .001);
                var obj = File.ReadAllLines(Path.Combine(folder, "model.obj"));
                Check(obj.Count(l => l.StartsWith("v ")) == 9, "OBJ lost vertices");
                Check(obj.Count(l => l.StartsWith("vt ")) == 9, "OBJ lost texture coordinates");
                Check(obj.Count(l => l.StartsWith("vn ")) == 9, "OBJ lost normals");
                Check(obj.Contains("vt 0.125 0.25"), "OBJ UV convention or locale changed");
                Check(obj.Contains("f 7/7/7 8/8/8 9/9/9"), "OBJ part indices not offset");
                Check(obj.Count(l => l.StartsWith("o ")) == 2, "OBJ merged parts");
                var mtl = File.ReadAllText(Path.Combine(folder, "model.mtl"));
                Check(mtl.Contains("map_Kd textures/first.png") && mtl.Contains("map_d textures/second_opacity.png"), "material texture links missing");
                Check(!mtl.Contains("C:") && !mtl.Contains("AppData"), "absolute cache path exported");
            }
            finally
            {
                if (Path.GetFileName(folder) != token || Path.GetDirectoryName(folder) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar))
                    throw new Exception("Unsafe temporary output directory");
                Directory.Delete(folder, true);
            }
            var saved = part.Uv; part.Uv = new double[] { 0, 0 };
            bool rejected = false; try { PortableModel.Validate(parts, materials, .001); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "missing UV accepted"); part.Uv = saved;
            saved = part.Positions; part.Positions = (double[])saved.Clone(); part.Positions[0] = double.NaN;
            rejected = false; try { PortableModel.Validate(parts, materials, .001); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "NaN position accepted"); part.Positions = saved;
            materials[0].ColorPath = "../outside.png";
            rejected = false; try { PortableModel.Validate(parts, materials, .001); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "nonportable image path accepted");
            int ink = unchecked((int)0xff397bbd);
            for (int a = 0; a <= 255; a++)
            {
                int mask = unchecked((int)0xff000000) | (a << 16) | (a << 8) | a;
                int pixel = PortableModel.TexturePixel(ink, mask, false);
                Check(((uint)pixel >> 24) == a, "portable opacity polarity");
                Check((pixel & 0x00ffffff) == (ink & 0x00ffffff), "portable tint or artwork faded toward white");
            }
            Check(PortableModel.TexturePixel(ink, null, false) == ink, "opaque PNG changed");
            Check(((uint)PortableModel.TexturePixel(unchecked((int)0x80397bbd), unchecked((int)0xff808080), true) >> 24) == 64,
                "source alpha and opacity mask not combined");
            UntexturedAndMixed();
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }
        Console.WriteLine("Portable UV export regression checks passed: " + checks);
    }

    private static void UntexturedAndMixed()
    {
        var materials = new[] {
            new PortableMaterial { Name = "solid brown", BaseColor = new double[] { .2, .1, .05, 1 } },
            new PortableMaterial { Name = "mapped one", ColorPath = "textures/mapped_one.png", Image = Png },
            new PortableMaterial { Name = "solid translucent", BaseColor = new double[] { .1, .3, .5, .6 } },
            new PortableMaterial { Name = "mapped two", ColorPath = "textures/mapped_two.png", Image = Png }
        };
        var parts = Enumerable.Range(0, 4).Select(i => new PortablePart {
            Name = "mixed part " + i, SourceId = "mixed-source-" + i, Material = i,
            Positions = new double[] { 1000*i,0,0, 1000*i+100,0,0, 1000*i,100,0 },
            Normals = new double[] { 0,0,1,0,0,1,0,0,1 },
            Uv = i % 2 == 0 ? null : new double[] { .1,.2,.8,.2,.1,.9 }, Triangles = new[] { 0,1,2 }
        }).ToArray();
        byte[] mixed;
        using (var stream = new MemoryStream())
        {
            PortableModel.WriteGlb(stream, parts, materials, .001);
            mixed = stream.ToArray();
            using (var json = GlbJson(mixed))
            {
                var root = json.RootElement;
                Check(root.GetProperty("nodes").GetArrayLength() == 4, "mixed export omitted untextured parts");
                Check(root.GetProperty("images").GetArrayLength() == 2 && root.GetProperty("textures").GetArrayLength() == 2, "solid material allocated a fake image");
                for (int i = 0; i < 4; i++)
                {
                    var primitive = root.GetProperty("meshes")[i].GetProperty("primitives")[0];
                    var mat = root.GetProperty("materials")[i];
                    var pbr = mat.GetProperty("pbrMetallicRoughness");
                    Check(primitive.GetProperty("material").GetInt32() == i, "mixed material assigned to wrong part");
                    Check(root.GetProperty("nodes")[i].GetProperty("extras").GetProperty("source_id").GetString() == parts[i].SourceId, "mixed source identity lost");
                    Check(primitive.GetProperty("attributes").TryGetProperty("TEXCOORD_0", out _) == (i % 2 == 1), "solid part requires a fabricated UV");
                    Check(pbr.TryGetProperty("baseColorTexture", out var texture) == (i % 2 == 1), "solid part inherited the previous texture");
                    if (i % 2 == 1) Check(texture.GetProperty("index").GetInt32() == i / 2, "mixed GLB texture index used material index");
                    for (int c = 0; c < 4; c++)
                        Check(Math.Abs(pbr.GetProperty("baseColorFactor")[c].GetDouble() - materials[i].BaseColor[c]) < 1e-12, "current material color/opacity changed");
                }
                Check(root.GetProperty("materials")[2].GetProperty("alphaMode").GetString() == "BLEND", "solid transparency discarded");
                Check(root.GetProperty("materials")[0].GetProperty("alphaMode").GetString() == "OPAQUE", "solid brown became transparent");
            }
        }
        var onlyParts = new[] { parts[0], parts[2] };
        onlyParts[1].Material = 1;
        var onlyMaterials = new[] { materials[0], materials[2] };
        byte[] untextured;
        using (var stream = new MemoryStream())
        {
            PortableModel.WriteGlb(stream, onlyParts, onlyMaterials, .001);
            untextured = stream.ToArray();
            using (var json = GlbJson(untextured))
            {
                var root = json.RootElement;
                Check(root.GetProperty("nodes").GetArrayLength() == 2, "untextured-only objects omitted");
                foreach (string field in new[] { "images", "textures", "samplers" })
                    Check(!root.TryGetProperty(field, out _), "empty or missing image reference in solid-only GLB");
                foreach (var mesh in root.GetProperty("meshes").EnumerateArray())
                    Check(!mesh.GetProperty("primitives")[0].GetProperty("attributes").TryGetProperty("TEXCOORD_0", out _), "solid-only GLB requires UVs");
            }
        }
        parts[2].Material = 2;
        var fixtureFolder = Path.Combine("DielineUV", "Tests", "artifacts");
        if (Directory.Exists(Path.Combine("DielineUV", "Tests")))
        {
            Directory.CreateDirectory(fixtureFolder);
            File.WriteAllBytes(Path.Combine(fixtureFolder, "portable-mixed-fixture.glb"), mixed);
            File.WriteAllBytes(Path.Combine(fixtureFolder, "portable-untextured-fixture.glb"), untextured);
        }
        string token = "DielineUV-mixed-" + Guid.NewGuid().ToString("N"), folder = Path.Combine(Path.GetTempPath(), token);
        Directory.CreateDirectory(folder);
        try
        {
            PortableModel.WriteObj(folder, parts, materials, .001);
            var obj = File.ReadAllLines(Path.Combine(folder, "model.obj"));
            Check(obj.Count(l => l.StartsWith("o ")) == 4, "OBJ mixed export omitted parts");
            Check(obj.Count(l => l.StartsWith("v ")) == 12 && obj.Count(l => l.StartsWith("vt ")) == 6, "mixed OBJ UV count assumes every part is textured");
            Check(obj.Contains("f 1//1 2//2 3//3"), "solid OBJ triangle incorrectly references UVs");
            Check(obj.Contains("f 4/1/4 5/2/5 6/3/6"), "first mapped OBJ UV indices affected by solid vertices");
            Check(obj.Contains("f 7//7 8//8 9//9"), "second solid OBJ triangle indices changed");
            Check(obj.Contains("f 10/4/10 11/5/11 12/6/12"), "second mapped OBJ UV index offset incorrect");
            var mtl = File.ReadAllLines(Path.Combine(folder, "model.mtl"));
            Check(mtl.Count(l => l.StartsWith("map_Kd ")) == 2, "solid MTL links to a missing PNG");
            Check(mtl.Contains("Kd 0.2 0.1 0.05") && mtl.Contains("d 0.6"), "solid MTL color/opacity discarded or wrong locale");
            parts[2].Material = 1;
            PortableModel.WriteObj(folder, onlyParts, onlyMaterials, .001);
            obj = File.ReadAllLines(Path.Combine(folder, "model.obj"));
            mtl = File.ReadAllLines(Path.Combine(folder, "model.mtl"));
            Check(obj.Count(l => l.StartsWith("o ")) == 2 && !obj.Any(l => l.StartsWith("vt ")), "solid-only OBJ needs UVs or omitted geometry");
            Check(!mtl.Any(l => l.StartsWith("map_")), "solid-only MTL contains dangling image paths");
        }
        finally
        {
            parts[2].Material = 2;
            if (Path.GetFileName(folder) != token || Path.GetDirectoryName(folder) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar))
                throw new Exception("Unsafe temporary output directory");
            Directory.Delete(folder, true);
        }
        var savedUv = parts[1].Uv; parts[1].Uv = null;
        bool rejected = false; try { PortableModel.Validate(parts, materials, .001); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "textured part without UV accepted after adding solid export"); parts[1].Uv = savedUv;
        materials[0].BaseColor[3] = 1.01;
        rejected = false; try { PortableModel.Validate(parts, materials, .001); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "invalid solid opacity accepted"); materials[0].BaseColor[3] = 1;
        materials[0].ColorPath = "textures/missing.png";
        rejected = false; try { PortableModel.Validate(parts, materials, .001); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "untextured material retained a missing texture reference"); materials[0].ColorPath = null;
        Check(PortableModel.LinearColor(0) == 0 && PortableModel.LinearColor(1) == 1, "material black/white color transfer");
        Check(Math.Abs(PortableModel.LinearColor(.5) - .21404114048223255) < 1e-12, "material sRGB color was not converted to glTF linear factor");
    }

    private static JsonDocument GlbJson(byte[] bytes)
    {
        return JsonDocument.Parse(Encoding.UTF8.GetString(bytes, 20, BitConverter.ToInt32(bytes, 12)));
    }

    private static float[] Floats(JsonElement root, byte[] bytes, int accessorIndex, int size)
    {
        var accessor = root.GetProperty("accessors")[accessorIndex];
        var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
        int start = view.GetProperty("byteOffset").GetInt32() + accessor.GetProperty("byteOffset").GetInt32();
        return Enumerable.Range(0, accessor.GetProperty("count").GetInt32() * size).Select(i => BitConverter.ToSingle(bytes, start + 4 * i)).ToArray();
    }
}
