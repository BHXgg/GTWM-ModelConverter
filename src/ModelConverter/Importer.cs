using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Assimp;
using Assimp.Configs;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace GTWM.ModelConverter
{
    /// <summary>Reads any format Assimp understands into a <see cref="SourceModel"/> at bind pose.</summary>
    internal static class Importer
    {
        public static SourceModel Import(string path, ConvertOptions options, List<string> log)
        {
            var ctx = new AssimpContext();
            ctx.SetConfig(new NormalSmoothingAngleConfig(66f));
            ctx.SetConfig(new FBXPreservePivotsConfig(false));
            const PostProcessSteps steps = PostProcessSteps.Triangulate | PostProcessSteps.JoinIdenticalVertices
                | PostProcessSteps.LimitBoneWeights | PostProcessSteps.GenerateSmoothNormals | PostProcessSteps.FlipUVs
                | PostProcessSteps.SortByPrimitiveType | PostProcessSteps.ValidateDataStructure;
            Scene scene;
            string importPath = path;
            if (Path.GetExtension(path).Equals(".vrm", StringComparison.OrdinalIgnoreCase))
            {
                // VRM is a glTF binary; Assimp only picks the reader by extension.
                importPath = Path.Combine(Path.GetTempPath(), "gtwm_" + Guid.NewGuid().ToString("N") + ".glb");
                File.Copy(path, importPath);
            }
            try
            {
                scene = ctx.ImportFile(importPath, steps);
            }
            catch (Exception e)
            {
                throw new InvalidDataException("Couldn't read the model: " + e.Message);
            }
            finally
            {
                if (importPath != path)
                {
                    try
                    {
                        File.Delete(importPath);
                    }
                    catch
                    {
                    }
                }
            }
            if (scene == null || !scene.HasMeshes)
            {
                throw new InvalidDataException("The file has no meshes.");
            }

            var src = new SourceModel();
            var globals = new List<Matrix4x4>();
            var byName = new Dictionary<string, int>();
            var assimpNodes = new List<Node>();
            AddNode(scene.RootNode, -1, Matrix4x4.Identity, src, globals, byName, assimpNodes);
            var restFromBind = new HashSet<int>();

            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            foreach (Material m in scene.Materials)
            {
                src.Materials.Add(ReadMaterial(m, scene, dir, log));
            }

            for (int node = 0; node < globals.Count; node++)
            {
                Node n = assimpNodes[node];
                if (n == null || !n.HasMeshes)
                {
                    continue;
                }
                Matrix4x4 meshGlobal = globals[node];
                Matrix4x4.Invert(meshGlobal, out Matrix4x4 meshGlobalInv);
                Matrix4x4 normalMatrix = Matrix4x4.Transpose(meshGlobalInv);
                foreach (int mi in n.MeshIndices)
                {
                    Mesh mesh = scene.Meshes[mi];
                    if (!mesh.HasFaces || mesh.PrimitiveType != PrimitiveType.Triangle && !mesh.Faces.Any(f => f.IndexCount == 3))
                    {
                        continue;
                    }
                    var part = new SourceModel.Part { Name = string.IsNullOrEmpty(mesh.Name) ? src.Names[node] : mesh.Name, Material = mesh.MaterialIndex, Skinned = mesh.HasBones };
                    for (int v = 0; v < mesh.VertexCount; v++)
                    {
                        Vector3D p = mesh.Vertices[v];
                        part.Positions.Add(Vector3.Transform(new Vector3(p.X, p.Y, p.Z), meshGlobal));
                        Vector3D nn = mesh.HasNormals ? mesh.Normals[v] : new Vector3D(0, 1, 0);
                        Vector3 normal = Vector3.TransformNormal(new Vector3(nn.X, nn.Y, nn.Z), normalMatrix);
                        part.Normals.Add(normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY);
                        if (mesh.HasTextureCoords(0))
                        {
                            Vector3D uv = mesh.TextureCoordinateChannels[0][v];
                            part.Uvs.Add(new Vector2(uv.X, uv.Y));
                        }
                        else
                        {
                            part.Uvs.Add(Vector2.Zero);
                        }
                        part.Weights.Add(new List<(int, float)>());
                    }
                    foreach (Face f in mesh.Faces)
                    {
                        if (f.IndexCount == 3)
                        {
                            part.Triangles.Add(f.Indices[0]);
                            part.Triangles.Add(f.Indices[1]);
                            part.Triangles.Add(f.Indices[2]);
                        }
                    }
                    if (mesh.HasBones)
                    {
                        foreach (Bone bone in mesh.Bones)
                        {
                            if (!byName.TryGetValue(bone.Name, out int bi))
                            {
                                continue;
                            }
                            src.Bones.Add(bi);
                            if (restFromBind.Add(bi))
                            {
                                // Bind pose of the bone: inverse(offset) in mesh space, then into the scene.
                                Matrix4x4.Invert(ToNumerics(bone.OffsetMatrix), out Matrix4x4 bind);
                                src.Rest[bi] = (bind * meshGlobal).Translation;
                            }
                            foreach (VertexWeight vw in bone.VertexWeights)
                            {
                                if (vw.VertexID < part.Weights.Count && vw.Weight > 0f)
                                {
                                    part.Weights[vw.VertexID].Add((bi, vw.Weight));
                                }
                            }
                        }
                    }
                    for (int v = 0; v < part.Weights.Count; v++)
                    {
                        if (part.Weights[v].Count == 0)
                        {
                            // Unskinned (or unweighted) vertices follow their node rigidly.
                            part.Weights[v].Add((node, 1f));
                        }
                    }
                    src.Parts.Add(part);
                }
            }
            if (src.Materials.Count == 0)
            {
                src.Materials.Add(new SourceModel.MaterialData { Name = "default", Color = new Vector4(0.8f, 0.8f, 0.8f, 1f) });
            }
            // Ancestors of bones are skeleton too (a Hips with no weights still counts).
            foreach (int b in src.Bones.ToList())
            {
                for (int i = src.Parent[b]; i >= 0; i = src.Parent[i])
                {
                    src.Bones.Add(i);
                }
            }
            log.Add($"Read {Path.GetFileName(path)}: {src.Parts.Count} mesh part(s), {src.Materials.Count} material(s), {src.Bones.Count} bone(s).");
            return src;
        }

        private static void AddNode(Node n, int parent, Matrix4x4 parentGlobal, SourceModel src, List<Matrix4x4> globals, Dictionary<string, int> byName, List<Node> all)
        {
            all.Add(n);
            Matrix4x4 global = ToNumerics(n.Transform) * parentGlobal;
            int index = src.Names.Count;
            src.Names.Add(n.Name ?? "node" + index);
            src.Parent.Add(parent);
            src.Rest.Add(global.Translation);
            globals.Add(global);
            if (n.Name != null && !byName.ContainsKey(n.Name))
            {
                byName[n.Name] = index;
            }
            if (n.HasChildren)
            {
                foreach (Node c in n.Children)
                {
                    AddNode(c, index, global, src, globals, byName, all);
                }
            }
        }

        /// <summary>Assimp is column-vector (translation in A4/B4/C4); System.Numerics is row-vector.</summary>
        private static Matrix4x4 ToNumerics(Assimp.Matrix4x4 m)
        {
            return new Matrix4x4(m.A1, m.B1, m.C1, m.D1, m.A2, m.B2, m.C2, m.D2, m.A3, m.B3, m.C3, m.D3, m.A4, m.B4, m.C4, m.D4);
        }

        private static SourceModel.MaterialData ReadMaterial(Material m, Scene scene, string dir, List<string> log)
        {
            var data = new SourceModel.MaterialData { Name = m.HasName ? m.Name : "material" };
            Color4D c = m.HasColorDiffuse ? m.ColorDiffuse : new Color4D(0.8f, 0.8f, 0.8f, 1f);
            float alpha = m.HasOpacity ? m.Opacity : c.A;
            TextureSlot slot;
            bool hasTexture = m.GetMaterialTexture(TextureType.Diffuse, 0, out slot) && !string.IsNullOrEmpty(slot.FilePath);
            if (hasTexture)
            {
                data.Texture = LoadTexture(slot.FilePath, scene, dir, log);
            }
            // With a texture, FBX's default 0.8 grey would just darken it.
            data.Color = data.Texture != null
                ? new Vector4(1f, 1f, 1f, Clamp01(alpha))
                : new Vector4(Linear(c.R), Linear(c.G), Linear(c.B), Clamp01(alpha));
            return data;
        }

        private static float Linear(float srgb) => (float)Math.Pow(Clamp01(srgb), 2.2);

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        private static byte[] LoadTexture(string reference, Scene scene, string dir, List<string> log)
        {
            EmbeddedTexture embedded = null;
            if (reference.StartsWith("*") && int.TryParse(reference.Substring(1), out int idx) && idx >= 0 && idx < scene.TextureCount)
            {
                embedded = scene.Textures[idx];
            }
            else if (scene.HasTextures)
            {
                string file = Path.GetFileName(reference.Replace('\\', '/'));
                embedded = scene.Textures.FirstOrDefault(t => !string.IsNullOrEmpty(t.Filename) && string.Equals(Path.GetFileName(t.Filename.Replace('\\', '/')), file, StringComparison.OrdinalIgnoreCase));
            }
            if (embedded != null)
            {
                if (embedded.IsCompressed)
                {
                    return embedded.CompressedData;
                }
                return Textures.FromTexels(embedded.Width, embedded.Height, embedded.NonCompressedData.Select(t => (t.R, t.G, t.B, t.A)).ToArray());
            }

            string found = FindFile(reference, dir);
            if (found == null)
            {
                log.Add($"Texture not found: {reference} (put it next to the model).");
                return null;
            }
            return File.ReadAllBytes(found);
        }

        private static string FindFile(string reference, string dir)
        {
            string clean = reference.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            string name = Path.GetFileName(clean);
            var candidates = new List<string>();
            try
            {
                if (Path.IsPathRooted(clean))
                {
                    candidates.Add(clean);
                }
                candidates.Add(Path.Combine(dir, clean));
            }
            catch (ArgumentException)
            {
            }
            candidates.Add(Path.Combine(dir, name));
            candidates.Add(Path.Combine(dir, "textures", name));
            candidates.Add(Path.Combine(dir, "Textures", name));
            candidates.Add(Path.Combine(Path.GetDirectoryName(dir) ?? dir, "textures", name));
            string hit = candidates.FirstOrDefault(File.Exists);
            if (hit != null)
            {
                return hit;
            }
            try
            {
                // Last resort: anywhere under the model's folder (zips often nest textures oddly).
                return Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }
    }
}
