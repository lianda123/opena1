using System;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.Geometry;
using Rhino.Render;

namespace DielineUV
{
    public sealed partial class DielineUVCommand
    {
        private sealed class AtlasRegion
        {
            public int X, Y, Width, Height;
            public Point2f Map(double u, double v, TextureAtlas atlas)
            {
                u = Math.Max(0, Math.Min(1, u));
                v = Math.Max(0, Math.Min(1, v));
                return new Point2f((float)((X + 0.5 + u * (Width - 1)) / atlas.Width),
                    (float)((atlas.Height - Y - Height + 0.5 + v * (Height - 1)) / atlas.Height));
            }
        }

        private sealed class TextureAtlas
        {
            public int Width, Height;
            public string Image, Artwork;
            public AtlasRegion Print, Back, Side;
            public bool BackColor, SideColor;
            public bool Alpha;
        }

        private static TextureAtlas CreateTextureAtlas(string image, SideFinish finish)
        {
            if (finish.Transparent) return CreateTransparentAtlas(image, finish);
            using (var artwork = new Eto.Drawing.Bitmap(image))
            {
                const int tile = 512, padding = 16;
                var atlas = new TextureAtlas { Width = artwork.Width + tile + 3 * padding,
                    Height = Math.Max(artwork.Height + 2 * padding, 2 * tile + 3 * padding),
                    Artwork = CacheTexture(image), Image = Path.Combine(TextureCache(), Guid.NewGuid().ToString("N") + ".png"),
                    Print = new AtlasRegion { X = padding, Y = padding, Width = artwork.Width, Height = artwork.Height },
                    Side = new AtlasRegion { X = artwork.Width + 2 * padding, Y = padding, Width = tile, Height = tile },
                    Back = new AtlasRegion { X = artwork.Width + 2 * padding, Y = tile + 2 * padding, Width = tile, Height = tile },
                    BackColor = finish.Back.Kind == "color", SideColor = finish.Sides.Kind == "color" };
                using (var bitmap = new Eto.Drawing.Bitmap(atlas.Width, atlas.Height, Eto.Drawing.PixelFormat.Format32bppRgb))
                {
                    using (var graphics = new Eto.Drawing.Graphics(bitmap))
                    {
                        graphics.Clear(Eto.Drawing.Colors.White);
                        graphics.DrawImage(artwork, new Eto.Drawing.RectangleF(atlas.Print.X, atlas.Print.Y, atlas.Print.Width, atlas.Print.Height));
                        DrawFinishTile(graphics, atlas.Side, finish.Sides);
                        DrawFinishTile(graphics, atlas.Back, finish.Back);
                    }
                    bitmap.Save(atlas.Image, Eto.Drawing.ImageFormat.Png);
                }
                return atlas;
            }
        }

        private static void DrawFinishTile(Eto.Drawing.Graphics graphics, AtlasRegion region, FinishSpec spec)
        {
            var rectangle = new Eto.Drawing.RectangleF(region.X, region.Y, region.Width, region.Height);
            if (spec.Kind == "texture")
            {
                using (var image = new Eto.Drawing.Bitmap(spec.Texture)) graphics.DrawImage(image, rectangle);
            }
            else
            {
                var color = Eto.Drawing.Color.FromArgb(spec.Color.R, spec.Color.G, spec.Color.B);
                graphics.FillRectangle(color, rectangle);
            }
        }

        private static void PrepareAtlasMapping(Change change, Mesh original, Part part, TextureAtlas atlas)
        {
            using (var combined = new Mesh())
            {
                if (change.FinishBrep == null)
                {
                    using (var mesh = original.DuplicateMesh())
                    {
                        for (int i = 0; i < mesh.TextureCoordinates.Count; i++)
                        {
                            var uv = mesh.TextureCoordinates[i];
                            mesh.TextureCoordinates.SetTextureCoordinate(i, atlas.Print.Map(uv.X, uv.Y, atlas));
                        }
                        combined.Append(mesh);
                    }
                }
                else
                {
                    var parameters = MeshingParameters.QualityRenderMesh;
                    parameters.JaggedSeams = true;
                    foreach (var face in change.FinishBrep.Faces)
                    {
                        bool print = change.PrintFaces.Contains(face.FaceIndex);
                        bool back = change.BackFaces != null && change.BackFaces.Contains(face.FaceIndex);
                        using (var singleFace = face.DuplicateFace(false))
                        {
                            var pieces = Mesh.CreateFromBrep(singleFace, parameters);
                            if (pieces == null || pieces.Length == 0)
                                throw new InvalidDataException("零件「" + part.Label + "」的面 " + face.FaceIndex + " 无法建立显示贴图。");
                            var bounds = singleFace.GetBoundingBox(true);
                            var min = new[] { bounds.Min.X, bounds.Min.Y, bounds.Min.Z };
                            var sizes = new[] { bounds.Max.X - min[0], bounds.Max.Y - min[1], bounds.Max.Z - min[2] };
                            var axes = Enumerable.Range(0, 3).OrderByDescending(i => sizes[i]).Take(2).ToArray();
                            try
                            {
                                foreach (var mesh in pieces)
                                {
                                    mesh.Normals.ComputeNormals();
                                    if (print || back)
                                    {
                                        mesh.SetTextureCoordinates(change.Mapping, Transform.Identity, false);
                                        if (mesh.TextureCoordinates.Count != mesh.Vertices.Count)
                                            throw new InvalidDataException("零件「" + part.Label + "」的原始刀模 UV 无法读取。");
                                    }
                                    else mesh.TextureCoordinates.Clear();
                                    for (int i = 0; i < mesh.Vertices.Count; i++)
                                    {
                                        Point2f uv;
                                        if (print)
                                        {
                                            var source = mesh.TextureCoordinates[i];
                                            uv = atlas.Print.Map(source.X, source.Y, atlas);
                                        }
                                        else if (back)
                                        {
                                            var source = mesh.TextureCoordinates[i];
                                            uv = atlas.Back.Map(atlas.BackColor ? 0.5 : Normalize(source.X, part.UvMin[0], part.UvMax[0]),
                                                atlas.BackColor ? 0.5 : Normalize(source.Y, part.UvMin[1], part.UvMax[1]), atlas);
                                        }
                                        else
                                        {
                                            var p = mesh.Vertices.Point3dAt(i);
                                            var coordinates = new[] { p.X, p.Y, p.Z };
                                            uv = atlas.Side.Map(atlas.SideColor ? 0.5 : Normalize(coordinates[axes[0]], min[axes[0]], min[axes[0]] + sizes[axes[0]]),
                                                atlas.SideColor ? 0.5 : Normalize(coordinates[axes[1]], min[axes[1]], min[axes[1]] + sizes[axes[1]]), atlas);
                                        }
                                        if (print || back) mesh.TextureCoordinates.SetTextureCoordinate(i, uv);
                                        else mesh.TextureCoordinates.Add(uv.X, uv.Y);
                                    }
                                    // Keep separate face vertices and UV islands at material seams.
                                    mesh.Normals.ComputeNormals();
                                    combined.Append(mesh);
                                }
                            }
                            finally { foreach (var mesh in pieces) mesh.Dispose(); }
                        }
                        face.ClearMaterialChannelIndex();
                    }
                }
                combined.Normals.ComputeNormals();
                combined.Compact();
                if (!combined.IsValid || combined.TextureCoordinates.Count != combined.Vertices.Count)
                    throw new InvalidDataException("零件「" + part.Label + "」的分区贴图数据不完整。");
                var mapping = TextureMapping.CreateCustomMeshMapping(combined);
                if (mapping == null) throw new InvalidOperationException("无法建立分区 UV。");
                change.Mapping.Dispose();
                change.Mapping = mapping;
            }
        }

        private static double Normalize(double value, double min, double max)
        {
            return max - min <= 1e-12 ? 0.5 : (value - min) / (max - min);
        }
    }
}
