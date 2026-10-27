using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json.Linq;

namespace GTWM.ModelConverter
{
    /// <summary>
    /// Offline check of the whole pipeline: a synthetic "game" skeleton (Unreal names, A-pose) and a
    /// synthetic Mixamo model (T-pose, centimetres, turned 90°), converted and measured.
    /// </summary>
    internal static class SelfTest
    {
        private static int _failures;

        public static int Run(string dir)
        {
            Directory.CreateDirectory(dir);
            AttachConsole(-1);
            string template = Path.Combine(dir, "template.glb");
            string mixamo = Path.Combine(dir, "mixamo_tpose.glb");
            string statue = Path.Combine(dir, "statue.obj");

            var tplBones = new (string name, string parent, Vector3 pos)[]
            {
                ("root", null, new Vector3(0, 0, 0)),
                ("pelvis", "root", new Vector3(0, 1.0f, 0)),
                ("spine_01", "pelvis", new Vector3(0, 1.12f, 0)),
                ("spine_02", "spine_01", new Vector3(0, 1.25f, 0)),
                ("spine_03", "spine_02", new Vector3(0, 1.38f, 0)),
                ("neck_01", "spine_03", new Vector3(0, 1.52f, 0)),
                ("head", "neck_01", new Vector3(0, 1.62f, 0)),
                ("clavicle_l", "spine_03", new Vector3(0.04f, 1.47f, 0.02f)),
                ("upperarm_l", "clavicle_l", new Vector3(0.17f, 1.45f, 0)),
                ("lowerarm_l", "upperarm_l", new Vector3(0.32f, 1.25f, 0)),
                ("hand_l", "lowerarm_l", new Vector3(0.46f, 1.06f, 0)),
                ("clavicle_r", "spine_03", new Vector3(-0.04f, 1.47f, 0.02f)),
                ("upperarm_r", "clavicle_r", new Vector3(-0.17f, 1.45f, 0)),
                ("lowerarm_r", "upperarm_r", new Vector3(-0.32f, 1.25f, 0)),
                ("hand_r", "lowerarm_r", new Vector3(-0.46f, 1.06f, 0)),
                ("thigh_l", "pelvis", new Vector3(0.1f, 0.95f, 0)),
                ("calf_l", "thigh_l", new Vector3(0.1f, 0.52f, 0.01f)),
                ("foot_l", "calf_l", new Vector3(0.1f, 0.09f, 0)),
                ("ball_l", "foot_l", new Vector3(0.1f, 0.02f, 0.12f)),
                ("thigh_r", "pelvis", new Vector3(-0.1f, 0.95f, 0)),
                ("calf_r", "thigh_r", new Vector3(-0.1f, 0.52f, 0.01f)),
                ("foot_r", "calf_r", new Vector3(-0.1f, 0.09f, 0)),
                ("ball_r", "foot_r", new Vector3(-0.1f, 0.02f, 0.12f)),
            };
            File.WriteAllBytes(template, BuildRig(tplBones, Matrix4x4.Identity, "head", new Vector3(0, 1.75f, 0)));

            var mixBones = new (string name, string parent, Vector3 pos)[]
            {
                ("mixamorig:Hips", null, new Vector3(0, 1.0f, 0)),
                ("mixamorig:Spine", "mixamorig:Hips", new Vector3(0, 1.1f, 0)),
                ("mixamorig:Spine1", "mixamorig:Spine", new Vector3(0, 1.22f, 0)),
                ("mixamorig:Spine2", "mixamorig:Spine1", new Vector3(0, 1.35f, 0)),
                ("mixamorig:Neck", "mixamorig:Spine2", new Vector3(0, 1.5f, 0)),
                ("mixamorig:Head", "mixamorig:Neck", new Vector3(0, 1.6f, 0)),
                ("mixamorig:HeadTop_End", "mixamorig:Head", new Vector3(0, 1.78f, 0)),
                ("mixamorig:LeftShoulder", "mixamorig:Spine2", new Vector3(0.05f, 1.45f, 0)),
                ("mixamorig:LeftArm", "mixamorig:LeftShoulder", new Vector3(0.15f, 1.45f, 0)),
                ("mixamorig:LeftForeArm", "mixamorig:LeftArm", new Vector3(0.42f, 1.45f, 0)),
                ("mixamorig:LeftHand", "mixamorig:LeftForeArm", new Vector3(0.66f, 1.45f, 0)),
                ("mixamorig:LeftHandIndex1", "mixamorig:LeftHand", new Vector3(0.74f, 1.45f, 0.02f)),
                ("mixamorig:RightShoulder", "mixamorig:Spine2", new Vector3(-0.05f, 1.45f, 0)),
                ("mixamorig:RightArm", "mixamorig:RightShoulder", new Vector3(-0.15f, 1.45f, 0)),
                ("mixamorig:RightForeArm", "mixamorig:RightArm", new Vector3(-0.42f, 1.45f, 0)),
                ("mixamorig:RightHand", "mixamorig:RightForeArm", new Vector3(-0.66f, 1.45f, 0)),
                ("mixamorig:RightHandIndex1", "mixamorig:RightHand", new Vector3(-0.74f, 1.45f, 0.02f)),
                ("mixamorig:LeftUpLeg", "mixamorig:Hips", new Vector3(0.09f, 0.93f, 0)),
                ("mixamorig:LeftLeg", "mixamorig:LeftUpLeg", new Vector3(0.09f, 0.5f, 0)),
                ("mixamorig:LeftFoot", "mixamorig:LeftLeg", new Vector3(0.09f, 0.08f, 0)),
                ("mixamorig:LeftToeBase", "mixamorig:LeftFoot", new Vector3(0.09f, 0.01f, 0.12f)),
                ("mixamorig:RightUpLeg", "mixamorig:Hips", new Vector3(-0.09f, 0.93f, 0)),
                ("mixamorig:RightLeg", "mixamorig:RightUpLeg", new Vector3(-0.09f, 0.5f, 0)),
                ("mixamorig:RightFoot", "mixamorig:RightLeg", new Vector3(-0.09f, 0.08f, 0)),
                ("mixamorig:RightToeBase", "mixamorig:RightFoot", new Vector3(-0.09f, 0.01f, 0.12f)),
            };
            // Centimetres, and facing sideways.
            Matrix4x4 skew = Matrix4x4.CreateRotationY((float)Math.PI / 2f) * Matrix4x4.CreateScale(100f);
            File.WriteAllBytes(mixamo, BuildRig(mixBones, skew, "mixamorig:Head", new Vector3(0, 1.78f, 0)));

            // Rigged humanoid.
            ConvertResult r = Converter.Convert(mixamo, new ConvertOptions { TemplatePath = template, Name = "test" });
            foreach (string line in r.Log)
            {
                Console.WriteLine("  " + line);
            }
            Check(r.Mode.StartsWith("Rigged"), "Mixamo rig recognised: " + r.Mode);
            Verify(r, "hand_l", new Vector3(0.46f, 1.06f, 0), 0.12f, "left hand lands on the game's left hand (A-pose, correct facing)");
            Verify(r, "hand_r", new Vector3(-0.46f, 1.06f, 0), 0.12f, "right hand lands on the game's right hand");
            Verify(r, "foot_l", new Vector3(0.1f, 0.06f, 0.04f), 0.12f, "left foot on the game's left foot");
            Verify(r, "head", new Vector3(0, 1.68f, 0), 0.12f, "head on the head");
            float minY = r.Vertices.Min(v => v.Y);
            Check(Math.Abs(minY - r.Template.Min.Y) < 0.02f, $"feet on the floor (min y {minY:0.000} vs {r.Template.Min.Y:0.000})");
            CheckGlb(r.Glb, r.Template);
            File.WriteAllBytes(Path.Combine(dir, "converted_mixamo.glb"), r.Glb);
            using (System.Drawing.Bitmap preview = Preview.Render(r, 1016, 660))
            {
                preview.Save(Path.Combine(dir, "preview.png"));
            }

            // Same rig through FBX (the format most people will use).
            string fbx = Path.Combine(dir, "mixamo_tpose.fbx");
            using (var ctx = new Assimp.AssimpContext())
            {
                Assimp.Scene scene = ctx.ImportFile(mixamo);
                Check(ctx.ExportFile(scene, fbx, "fbx"), "wrote FBX test file");
            }
            ConvertResult f = Converter.Convert(fbx, new ConvertOptions { TemplatePath = template, Name = "fbx" });
            Check(f.Mode.StartsWith("Rigged"), "FBX rig recognised: " + f.Mode);
            Verify(f, "hand_l", new Vector3(0.46f, 1.06f, 0), 0.12f, "FBX: left hand on the game's left hand");
            Verify(f, "foot_r", new Vector3(-0.1f, 0.06f, 0.04f), 0.12f, "FBX: right foot on the game's right foot");

            // Unrigged statue, Z-up OBJ.
            File.WriteAllText(statue, StatueObj());
            ConvertResult s = Converter.Convert(statue, new ConvertOptions { TemplatePath = template, Name = "statue" });
            foreach (string line in s.Log)
            {
                Console.WriteLine("  " + line);
            }
            Check(s.Mode.StartsWith("Static"), "static model detected: " + s.Mode);
            float h = s.Vertices.Max(v => v.Y) - s.Vertices.Min(v => v.Y);
            Check(Math.Abs(h - r.Template.Height) < 0.02f, $"Z-up statue stood up and scaled to height ({h:0.000} vs {r.Template.Height:0.000})");
            CheckGlb(s.Glb, s.Template);

            Console.WriteLine(_failures == 0 ? "ALL PASSED" : $"{_failures} FAILED");
            return _failures;
        }

        private static void Verify(ConvertResult r, string joint, Vector3 expected, float tolerance, string what)
        {
            GlbDocument doc = GlbDocument.Load(r.Glb);
            int jointNode = doc.Nodes.Select((n, i) => ((string)n["name"], i)).First(x => x.Item1 == joint).i;
            int jointIndex = ((JArray)doc.Json["skins"][0]["joints"]).Select(x => (int)x).ToList().IndexOf(jointNode);
            var hits = new List<Vector3>();
            foreach (JToken prim in (JArray)doc.Json["meshes"][0]["primitives"])
            {
                float[] p = doc.ReadFloats((int)prim["attributes"]["POSITION"], out int count, out _);
                float[] j = doc.ReadFloats((int)prim["attributes"]["JOINTS_0"], out _, out _);
                float[] w = doc.ReadFloats((int)prim["attributes"]["WEIGHTS_0"], out _, out _);
                for (int v = 0; v < count; v++)
                {
                    if ((int)j[v * 4] == jointIndex && w[v * 4] > 0.9f)
                    {
                        hits.Add(new Vector3(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]));
                    }
                }
            }
            if (hits.Count == 0)
            {
                Check(false, what + " (no vertices bound to " + joint + ")");
                return;
            }
            Vector3 c = hits.Aggregate(Vector3.Zero, (a, b) => a + b) / hits.Count;
            Check(Vector3.Distance(c, expected) < tolerance, $"{what}: {Fmt(c)} vs {Fmt(expected)}");
        }

        private static void CheckGlb(byte[] glb, Template tpl)
        {
            GlbDocument doc = GlbDocument.Load(glb);
            var joints = (JArray)doc.Json["skins"][0]["joints"];
            bool weightsOk = true;
            foreach (JToken prim in (JArray)doc.Json["meshes"][0]["primitives"])
            {
                float[] w = doc.ReadFloats((int)prim["attributes"]["WEIGHTS_0"], out int count, out _);
                float[] j = doc.ReadFloats((int)prim["attributes"]["JOINTS_0"], out _, out _);
                for (int v = 0; v < count; v++)
                {
                    float sum = w[v * 4] + w[v * 4 + 1] + w[v * 4 + 2] + w[v * 4 + 3];
                    weightsOk &= Math.Abs(sum - 1f) < 1e-3f && j.Skip(v * 4).Take(4).All(x => x < joints.Count);
                }
            }
            Check(joints.Count == tpl.Joints.Length && weightsOk, $"output skin valid ({joints.Count} joints, weights normalised)");
            var names = doc.Nodes.Select(n => (string)n["name"]).ToList();
            Check(tpl.Joints.All(i => names.Contains(tpl.Names[i])), "every game bone name preserved");
        }

        private static string Fmt(Vector3 v) => $"({v.X:0.00}, {v.Y:0.00}, {v.Z:0.00})";

        private static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
            if (!ok)
            {
                _failures++;
            }
        }

        /// <summary>A skinned rig with a box around each bone (weighted 100% to it), positions transformed by <paramref name="world"/>.</summary>
        private static byte[] BuildRig((string name, string parent, Vector3 pos)[] bones, Matrix4x4 world, string headBone, Vector3 headTop)
        {
            var w = new GlbWriter();
            var index = bones.Select((b, i) => (b.name, i)).ToDictionary(x => x.name, x => x.i);
            Vector3 P(int i) => Vector3.Transform(bones[i].pos, world);
            var nodes = new JArray();
            for (int i = 0; i < bones.Length; i++)
            {
                Vector3 local = bones[i].parent == null ? P(i) : P(i) - P(index[bones[i].parent]);
                var node = new JObject { ["name"] = bones[i].name, ["translation"] = new JArray(local.X, local.Y, local.Z) };
                var kids = bones.Select((b, k) => (b, k)).Where(x => x.b.parent == bones[i].name).Select(x => x.k).ToList();
                if (kids.Count > 0)
                {
                    node["children"] = new JArray(kids);
                }
                nodes.Add(node);
            }
            var pos = new List<Vector3>();
            var nrm = new List<Vector3>();
            var uv = new List<Vector2>();
            var js = new List<ushort[]>();
            var ws = new List<Vector4>();
            var tris = new List<int>();
            float scale = world.GetDeterminantScale();
            for (int i = 0; i < bones.Length; i++)
            {
                var kids = bones.Where(b => b.parent == bones[i].name).Select(b => index[b.name]).ToList();
                Vector3 a = bones[i].pos;
                Vector3 b2 = bones[i].name == headBone ? headTop : kids.Count == 1 ? bones[kids[0]].pos : a;
                if (kids.Count > 1 || a == b2)
                {
                    continue; // Branching bones (hips, chest) get no box of their own.
                }
                AddBox(Vector3.Min(a, b2) - new Vector3(0.035f), Vector3.Max(a, b2) + new Vector3(0.035f), world, (ushort)i, pos, nrm, uv, js, ws, tris);
            }
            var ibm = Enumerable.Range(0, bones.Length).Select(i => Matrix4x4.CreateTranslation(-P(i))).ToList();
            var attrs = new JObject
            {
                ["POSITION"] = w.Vec3(pos, true), ["NORMAL"] = w.Vec3(nrm), ["TEXCOORD_0"] = w.Vec2(uv),
                ["JOINTS_0"] = w.Joints(js), ["WEIGHTS_0"] = w.Vec4(ws),
            };
            nodes.Add(new JObject { ["name"] = "body", ["mesh"] = 0, ["skin"] = 0 });
            var root = new JObject
            {
                ["asset"] = new JObject { ["version"] = "2.0" },
                ["scene"] = 0,
                ["scenes"] = new JArray(new JObject { ["nodes"] = new JArray(0, nodes.Count - 1) }),
                ["nodes"] = nodes,
                ["meshes"] = new JArray(new JObject { ["primitives"] = new JArray(new JObject { ["attributes"] = attrs, ["indices"] = w.Indices(tris) }) }),
                ["skins"] = new JArray(new JObject { ["joints"] = new JArray(Enumerable.Range(0, bones.Length)), ["inverseBindMatrices"] = w.Matrices(ibm) }),
            };
            return w.Build(root);
        }

        private static float GetDeterminantScale(this Matrix4x4 m) => (float)Math.Pow(Math.Abs(m.GetDeterminant()), 1.0 / 3.0);

        private static void AddBox(Vector3 lo, Vector3 hi, Matrix4x4 world, ushort joint, List<Vector3> pos, List<Vector3> nrm, List<Vector2> uv, List<ushort[]> js, List<Vector4> ws, List<int> tris)
        {
            Vector3[] c =
            {
                new Vector3(lo.X, lo.Y, lo.Z), new Vector3(hi.X, lo.Y, lo.Z), new Vector3(hi.X, hi.Y, lo.Z), new Vector3(lo.X, hi.Y, lo.Z),
                new Vector3(lo.X, lo.Y, hi.Z), new Vector3(hi.X, lo.Y, hi.Z), new Vector3(hi.X, hi.Y, hi.Z), new Vector3(lo.X, hi.Y, hi.Z),
            };
            int[][] faces = { new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 }, new[] { 3, 7, 6, 2 }, new[] { 0, 4, 7, 3 }, new[] { 1, 2, 6, 5 } };
            foreach (int[] f in faces)
            {
                int b = pos.Count;
                Vector3 n = Vector3.Normalize(Vector3.Cross(c[f[1]] - c[f[0]], c[f[2]] - c[f[0]]));
                foreach (int k in f)
                {
                    pos.Add(Vector3.Transform(c[k], world));
                    nrm.Add(Vector3.Normalize(Vector3.TransformNormal(n, world)));
                    uv.Add(Vector2.Zero);
                    js.Add(new ushort[] { joint, 0, 0, 0 });
                    ws.Add(new Vector4(1, 0, 0, 0));
                }
                tris.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
            }
        }

        private static string StatueObj()
        {
            // A 0.5 × 0.3 × 2.0 block standing on Z (3ds Max / Z-up style).
            return string.Join("\n",
                "v -0.25 -0.15 0", "v 0.25 -0.15 0", "v 0.25 0.15 0", "v -0.25 0.15 0",
                "v -0.25 -0.15 2", "v 0.25 -0.15 2", "v 0.25 0.15 2", "v -0.25 0.15 2",
                "f 1 4 3 2", "f 5 6 7 8", "f 1 2 6 5", "f 2 3 7 6", "f 3 4 8 7", "f 4 1 5 8", "");
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);
    }
}
