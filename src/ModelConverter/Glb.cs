using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GTWM.ModelConverter
{
    /// <summary>
    /// A parsed .glb: JSON plus binary chunk, with accessor helpers. Matrices use System.Numerics
    /// (row vectors), so a glTF column-major array maps straight onto M11..M44 in order.
    /// </summary>
    internal sealed class GlbDocument
    {
        public JObject Json;
        public byte[] Bin = new byte[0];

        public JArray Nodes => Json["nodes"] as JArray ?? new JArray();

        public static GlbDocument Load(byte[] glb)
        {
            if (glb.Length < 20 || BitConverter.ToUInt32(glb, 0) != 0x46546C67)
            {
                throw new InvalidDataException("Not a .glb file.");
            }
            var doc = new GlbDocument();
            int pos = 12;
            while (pos + 8 <= glb.Length)
            {
                int length = BitConverter.ToInt32(glb, pos);
                uint type = BitConverter.ToUInt32(glb, pos + 4);
                if (type == 0x4E4F534A)
                {
                    doc.Json = JObject.Parse(Encoding.UTF8.GetString(glb, pos + 8, length));
                }
                else if (type == 0x004E4942)
                {
                    doc.Bin = new byte[length];
                    Buffer.BlockCopy(glb, pos + 8, doc.Bin, 0, length);
                }
                pos += 8 + length;
            }
            if (doc.Json == null)
            {
                throw new InvalidDataException("The .glb has no JSON.");
            }
            return doc;
        }

        public static Matrix4x4 LocalMatrix(JToken node)
        {
            if (node["matrix"] is JArray m && m.Count == 16)
            {
                return new Matrix4x4(
                    (float)m[0], (float)m[1], (float)m[2], (float)m[3],
                    (float)m[4], (float)m[5], (float)m[6], (float)m[7],
                    (float)m[8], (float)m[9], (float)m[10], (float)m[11],
                    (float)m[12], (float)m[13], (float)m[14], (float)m[15]);
            }
            Vector3 t = node["translation"] is JArray ta ? new Vector3((float)ta[0], (float)ta[1], (float)ta[2]) : Vector3.Zero;
            Quaternion q = node["rotation"] is JArray ra ? new Quaternion((float)ra[0], (float)ra[1], (float)ra[2], (float)ra[3]) : Quaternion.Identity;
            Vector3 s = node["scale"] is JArray sa ? new Vector3((float)sa[0], (float)sa[1], (float)sa[2]) : Vector3.One;
            return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(t);
        }

        public float[] ReadFloats(int accessorIndex, out int count, out int components)
        {
            JToken acc = Json["accessors"][accessorIndex];
            count = (int)acc["count"];
            components = Comps((string)acc["type"]);
            int ct = (int)acc["componentType"];
            int size = ct == 5126 || ct == 5125 ? 4 : ct == 5123 || ct == 5122 ? 2 : 1;
            bool normalized = acc["normalized"] != null && (bool)acc["normalized"];
            var result = new float[count * components];
            if (acc["bufferView"] == null)
            {
                return result;
            }
            JToken view = Json["bufferViews"][(int)acc["bufferView"]];
            int stride = view["byteStride"] != null ? (int)view["byteStride"] : components * size;
            int start = (view["byteOffset"] != null ? (int)view["byteOffset"] : 0) + (acc["byteOffset"] != null ? (int)acc["byteOffset"] : 0);
            for (int i = 0; i < count; i++)
            {
                for (int c = 0; c < components; c++)
                {
                    int o = start + i * stride + c * size;
                    float v;
                    switch (ct)
                    {
                        case 5126: v = BitConverter.ToSingle(Bin, o); break;
                        case 5125: v = BitConverter.ToUInt32(Bin, o); break;
                        case 5123: v = BitConverter.ToUInt16(Bin, o); if (normalized) v /= 65535f; break;
                        case 5122: v = BitConverter.ToInt16(Bin, o); if (normalized) v = Math.Max(v / 32767f, -1f); break;
                        case 5121: v = Bin[o]; if (normalized) v /= 255f; break;
                        default: v = (sbyte)Bin[o]; if (normalized) v = Math.Max(v / 127f, -1f); break;
                    }
                    result[i * components + c] = v;
                }
            }
            return result;
        }

        public Matrix4x4[] ReadMatrices(int accessorIndex)
        {
            float[] f = ReadFloats(accessorIndex, out int count, out _);
            var result = new Matrix4x4[count];
            for (int i = 0; i < count; i++)
            {
                int o = i * 16;
                result[i] = new Matrix4x4(f[o], f[o + 1], f[o + 2], f[o + 3], f[o + 4], f[o + 5], f[o + 6], f[o + 7],
                    f[o + 8], f[o + 9], f[o + 10], f[o + 11], f[o + 12], f[o + 13], f[o + 14], f[o + 15]);
            }
            return result;
        }

        private static int Comps(string type)
        {
            switch (type)
            {
                case "SCALAR": return 1;
                case "VEC2": return 2;
                case "VEC3": return 3;
                case "VEC4": return 4;
                case "MAT4": return 16;
                default: throw new InvalidDataException("Unsupported accessor type " + type);
            }
        }
    }

    /// <summary>Accumulates buffer views and accessors, then writes a .glb.</summary>
    internal sealed class GlbWriter
    {
        private readonly MemoryStream _bin = new MemoryStream();
        private readonly JArray _views = new JArray();
        private readonly JArray _accessors = new JArray();

        public int AddView(byte[] data, int? target = null)
        {
            while (_bin.Length % 4 != 0)
            {
                _bin.WriteByte(0);
            }
            var view = new JObject { ["buffer"] = 0, ["byteOffset"] = _bin.Length, ["byteLength"] = data.Length };
            if (target != null)
            {
                view["target"] = target.Value;
            }
            _bin.Write(data, 0, data.Length);
            _views.Add(view);
            return _views.Count - 1;
        }

        public int Vec3(IList<Vector3> v, bool minMax = false)
        {
            var f = new float[v.Count * 3];
            for (int i = 0; i < v.Count; i++)
            {
                f[i * 3] = v[i].X;
                f[i * 3 + 1] = v[i].Y;
                f[i * 3 + 2] = v[i].Z;
            }
            int a = Floats(f, v.Count, "VEC3", 34962);
            if (minMax && v.Count > 0)
            {
                _accessors[a]["min"] = new JArray(v.Min(p => p.X), v.Min(p => p.Y), v.Min(p => p.Z));
                _accessors[a]["max"] = new JArray(v.Max(p => p.X), v.Max(p => p.Y), v.Max(p => p.Z));
            }
            return a;
        }

        public int Vec2(IList<Vector2> v)
        {
            var f = new float[v.Count * 2];
            for (int i = 0; i < v.Count; i++)
            {
                f[i * 2] = v[i].X;
                f[i * 2 + 1] = v[i].Y;
            }
            return Floats(f, v.Count, "VEC2", 34962);
        }

        public int Vec4(IList<Vector4> v)
        {
            var f = new float[v.Count * 4];
            for (int i = 0; i < v.Count; i++)
            {
                f[i * 4] = v[i].X;
                f[i * 4 + 1] = v[i].Y;
                f[i * 4 + 2] = v[i].Z;
                f[i * 4 + 3] = v[i].W;
            }
            return Floats(f, v.Count, "VEC4", 34962);
        }

        public int Matrices(IList<Matrix4x4> m)
        {
            var f = new float[m.Count * 16];
            for (int i = 0; i < m.Count; i++)
            {
                Matrix4x4 x = m[i];
                float[] row = { x.M11, x.M12, x.M13, x.M14, x.M21, x.M22, x.M23, x.M24, x.M31, x.M32, x.M33, x.M34, x.M41, x.M42, x.M43, x.M44 };
                Array.Copy(row, 0, f, i * 16, 16);
            }
            return Floats(f, m.Count, "MAT4", null);
        }

        public int Joints(IList<ushort[]> j)
        {
            var bytes = new byte[j.Count * 8];
            for (int i = 0; i < j.Count; i++)
            {
                for (int k = 0; k < 4; k++)
                {
                    BitConverter.GetBytes(j[i][k]).CopyTo(bytes, i * 8 + k * 2);
                }
            }
            _accessors.Add(new JObject { ["bufferView"] = AddView(bytes, 34962), ["componentType"] = 5123, ["count"] = j.Count, ["type"] = "VEC4" });
            return _accessors.Count - 1;
        }

        public int Indices(IList<int> idx)
        {
            var bytes = new byte[idx.Count * 4];
            for (int i = 0; i < idx.Count; i++)
            {
                BitConverter.GetBytes((uint)idx[i]).CopyTo(bytes, i * 4);
            }
            _accessors.Add(new JObject { ["bufferView"] = AddView(bytes, 34963), ["componentType"] = 5125, ["count"] = idx.Count, ["type"] = "SCALAR" });
            return _accessors.Count - 1;
        }

        private int Floats(float[] data, int count, string type, int? target)
        {
            var bytes = new byte[data.Length * 4];
            Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            _accessors.Add(new JObject { ["bufferView"] = AddView(bytes, target), ["componentType"] = 5126, ["count"] = count, ["type"] = type });
            return _accessors.Count - 1;
        }

        public byte[] Build(JObject root)
        {
            while (_bin.Length % 4 != 0)
            {
                _bin.WriteByte(0);
            }
            root["buffers"] = new JArray(new JObject { ["byteLength"] = _bin.Length });
            root["bufferViews"] = _views;
            root["accessors"] = _accessors;
            byte[] json = Encoding.UTF8.GetBytes(root.ToString(Formatting.None));
            int padded = (json.Length + 3) & ~3;
            byte[] bin = _bin.ToArray();
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(0x46546C67u);
                w.Write(2u);
                w.Write((uint)(12 + 8 + padded + 8 + bin.Length));
                w.Write((uint)padded);
                w.Write(0x4E4F534Au);
                w.Write(json);
                for (int i = json.Length; i < padded; i++)
                {
                    w.Write((byte)0x20);
                }
                w.Write((uint)bin.Length);
                w.Write(0x004E4942u);
                w.Write(bin);
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
