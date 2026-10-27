using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GTWM.ModelConverter
{
    internal enum Slot
    {
        Hips, Neck, Head,
        LShoulder, LUpperArm, LLowerArm, LHand, LUpperLeg, LLowerLeg, LFoot, LToes,
        RShoulder, RUpperArm, RLowerArm, RHand, RUpperLeg, RLowerLeg, RFoot, RToes,
        LThumb1, LThumb2, LThumb3, LIndex1, LIndex2, LIndex3, LMiddle1, LMiddle2, LMiddle3, LRing1, LRing2, LRing3, LLittle1, LLittle2, LLittle3,
        RThumb1, RThumb2, RThumb3, RIndex1, RIndex2, RIndex3, RMiddle1, RMiddle2, RMiddle3, RRing1, RRing2, RRing3, RLittle1, RLittle2, RLittle3,
    }

    /// <summary>Humanoid bones of a skeleton: named slots plus the spine chain between hips and neck.</summary>
    internal sealed class HumanoidMap
    {
        public readonly Dictionary<Slot, int> Slots = new Dictionary<Slot, int>();
        public readonly List<int> Spine = new List<int>();

        public bool IsHumanoid => Slots.ContainsKey(Slot.Hips) && Limbs >= 2;

        public int Limbs => new[] { Slot.LUpperArm, Slot.RUpperArm, Slot.LUpperLeg, Slot.RUpperLeg }.Count(Slots.ContainsKey);

        public int? Get(Slot s) => Slots.TryGetValue(s, out int i) ? i : (int?)null;

        public override string ToString() => $"{Slots.Count} humanoid bones, spine of {Spine.Count}";
    }

    /// <summary>
    /// Recognises humanoid bones by name across the common rigs: Mixamo, Unity/VRM humanoid,
    /// VRoid, Unreal, Blender (metarig/Rigify), 3ds Max Biped, Valve, CC/iClone, MMD-ish English.
    /// </summary>
    internal static class Humanoid
    {
        private static readonly string[] Prefixes =
        {
            "valvebiped.bip01", "mixamorig", "bip001", "bip01", "cc_base_", "j_bip_c_", "j_bip_l_", "j_bip_r_", "j_bip_", "j_sec_",
            "def-", "def_", "org-", "rig_", "armature_", "skeleton_", "char_", "b_", "bn_",
        };

        public static HumanoidMap Classify(IList<string> names, IList<int> parent, ISet<int> candidates)
        {
            var map = new HumanoidMap();
            var depth = names.Select((_, i) => Depth(i, parent)).ToArray();
            // Shallowest match wins for every slot (so "Hand" beats "HandIndex1" and helper bones).
            foreach (int i in candidates.OrderBy(i => depth[i]))
            {
                Slot? slot = SlotOf(names[i]);
                if (slot != null && !map.Slots.ContainsKey(slot.Value))
                {
                    map.Slots[slot.Value] = i;
                }
            }
            // Mixamo/Unity "LeftArm" + "LeftForeArm": guard against both landing on the same bone family
            // by requiring limb parts to be ordered parent → child.
            FixChain(map, parent, Slot.LUpperArm, Slot.LLowerArm, Slot.LHand);
            FixChain(map, parent, Slot.RUpperArm, Slot.RLowerArm, Slot.RHand);
            FixChain(map, parent, Slot.LUpperLeg, Slot.LLowerLeg, Slot.LFoot);
            FixChain(map, parent, Slot.RUpperLeg, Slot.RLowerLeg, Slot.RFoot);

            if (!map.Slots.ContainsKey(Slot.Hips))
            {
                // No explicit hips: the common ancestor of both thighs is the pelvis.
                int? l = map.Get(Slot.LUpperLeg), r = map.Get(Slot.RUpperLeg);
                if (l != null && r != null)
                {
                    int? common = CommonAncestor(l.Value, r.Value, parent);
                    if (common != null)
                    {
                        map.Slots[Slot.Hips] = common.Value;
                    }
                }
            }

            // Spine: bones strictly between hips and neck (or head, or the arms' common ancestor).
            int? hips = map.Get(Slot.Hips);
            int? top = map.Get(Slot.Neck) ?? map.Get(Slot.Head) ?? CommonAncestorOpt(map.Get(Slot.LUpperArm), map.Get(Slot.RUpperArm), parent);
            if (hips != null && top != null)
            {
                var chain = new List<int>();
                bool reached = false;
                for (int i = top.Value; i >= 0; i = parent[i])
                {
                    if (i == hips.Value)
                    {
                        reached = true;
                        break;
                    }
                    if (i != top.Value || (map.Get(Slot.Neck) == null && map.Get(Slot.Head) == null))
                    {
                        chain.Add(i);
                    }
                }
                if (reached)
                {
                    chain.Reverse();
                    map.Spine.AddRange(chain.Where(i => i != map.Get(Slot.Neck) && i != map.Get(Slot.Head)));
                }
            }
            return map;
        }

        private static void FixChain(HumanoidMap map, IList<int> parent, Slot upper, Slot lower, Slot end)
        {
            int? u = map.Get(upper), l = map.Get(lower), e = map.Get(end);
            if (u != null && l != null && !IsAncestor(u.Value, l.Value, parent))
            {
                map.Slots.Remove(lower);
            }
            if (l != null && e != null && map.Slots.ContainsKey(lower) && !IsAncestor(l.Value, e.Value, parent))
            {
                map.Slots.Remove(end);
            }
        }

        /// <summary>Which humanoid slot a bone name means, if any.</summary>
        public static Slot? SlotOf(string raw)
        {
            string n = raw.ToLowerInvariant();
            int cut = Math.Max(n.LastIndexOf(':'), n.LastIndexOf('|'));
            if (cut >= 0)
            {
                n = n.Substring(cut + 1);
            }
            // VRoid "J_Bip_L_UpperArm": the side lives in the prefix.
            char? side = n.StartsWith("j_bip_l_") ? 'l' : n.StartsWith("j_bip_r_") ? 'r' : (char?)null;
            foreach (string p in Prefixes)
            {
                if (n.StartsWith(p))
                {
                    n = n.Substring(p.Length);
                    break;
                }
            }
            n = n.Trim(' ', '_', '.', '-');
            if (Regex.IsMatch(n, @"(^|[^a-z])(end|nub|tip|top|null|twist|roll|helper|ik|pole|target|ctrl|mch)([^a-z]|$)|_end$|end$|nub$|twist|_ik"))
            {
                return null;
            }

            side = side ?? SideOf(n);
            string core = Regex.Replace(n, @"(^|[\s_.\-])(left|right|l|r)($|[\s_.\-])", " ");
            core = core.Replace("left", "").Replace("right", "");
            core = Regex.Replace(core, @"[^a-z0-9]", "");

            Slot? finger = FingerSlot(core, side);
            if (finger != null)
            {
                return finger;
            }
            if (Regex.IsMatch(core, @"finger|thumb|index|middle|ring|pinky|little|metacarpal|palm|carpal"))
            {
                return null;
            }

            if (side == null)
            {
                if (core == "hips" || core == "hip" || core.StartsWith("pelvis"))
                {
                    return Slot.Hips;
                }
                if (core.StartsWith("neck"))
                {
                    return core == "neck" || core == "neck1" || core == "neck01" ? Slot.Neck : (Slot?)null;
                }
                if (core == "head" || core == "head1" || core == "head01")
                {
                    return Slot.Head;
                }
                return null;
            }

            bool l = side == 'l';
            if (Regex.IsMatch(core, @"forearm|lowerarm|elbow|lowarm"))
            {
                return l ? Slot.LLowerArm : Slot.RLowerArm;
            }
            if (Regex.IsMatch(core, @"upperarm|uparm|^arm\d*$|^arm"))
            {
                return l ? Slot.LUpperArm : Slot.RUpperArm;
            }
            if (Regex.IsMatch(core, @"^hand\d*$|wrist|^hand"))
            {
                return l ? Slot.LHand : Slot.RHand;
            }
            if (Regex.IsMatch(core, @"clavicle|shoulder|collar"))
            {
                return l ? Slot.LShoulder : Slot.RShoulder;
            }
            if (Regex.IsMatch(core, @"thigh|upleg|upperleg|femur"))
            {
                return l ? Slot.LUpperLeg : Slot.RUpperLeg;
            }
            if (Regex.IsMatch(core, @"calf|shin|lowerleg|lowleg|knee|^leg\d*$|^leg"))
            {
                return l ? Slot.LLowerLeg : Slot.RLowerLeg;
            }
            if (Regex.IsMatch(core, @"^toe|toebase|^ball|toes"))
            {
                return l ? Slot.LToes : Slot.RToes;
            }
            if (Regex.IsMatch(core, @"foot|ankle"))
            {
                return l ? Slot.LFoot : Slot.RFoot;
            }
            return null;
        }

        private static char? SideOf(string n)
        {
            if (n.Contains("left") || Regex.IsMatch(n, @"(^|[\s_.\-])l($|[\s_.\-\d])"))
            {
                return 'l';
            }
            if (n.Contains("right") || Regex.IsMatch(n, @"(^|[\s_.\-])r($|[\s_.\-\d])"))
            {
                return 'r';
            }
            return null;
        }

        private static Slot? FingerSlot(string core, char? side)
        {
            if (side == null)
            {
                return null;
            }
            Match m = Regex.Match(core, @"(thumb|index|middle|ring|pinky|little)(?:finger)?0?([0-3])$");
            if (!m.Success)
            {
                return null;
            }
            string finger = m.Groups[1].Value == "pinky" ? "Little" : char.ToUpper(m.Groups[1].Value[0]) + m.Groups[1].Value.Substring(1);
            int n = int.Parse(m.Groups[2].Value);
            if (n == 0)
            {
                n = 1; // Some rigs count from 0.
            }
            return Enum.TryParse((side == 'l' ? "L" : "R") + finger + n, out Slot s) ? s : (Slot?)null;
        }

        private static int Depth(int i, IList<int> parent)
        {
            int d = 0;
            for (; i >= 0 && d < 512; i = parent[i])
            {
                d++;
            }
            return d;
        }

        public static bool IsAncestor(int ancestor, int node, IList<int> parent)
        {
            for (int i = parent[node]; i >= 0; i = parent[i])
            {
                if (i == ancestor)
                {
                    return true;
                }
            }
            return false;
        }

        private static int? CommonAncestorOpt(int? a, int? b, IList<int> parent) => a != null && b != null ? CommonAncestor(a.Value, b.Value, parent) : null;

        private static int? CommonAncestor(int a, int b, IList<int> parent)
        {
            var seen = new HashSet<int>();
            for (int i = parent[a]; i >= 0; i = parent[i])
            {
                seen.Add(i);
            }
            for (int i = parent[b]; i >= 0; i = parent[i])
            {
                if (seen.Contains(i))
                {
                    return i;
                }
            }
            return null;
        }

        /// <summary>For posing: which slot each bone should point at.</summary>
        public static Slot? AimSlot(Slot s)
        {
            switch (s)
            {
                case Slot.Neck: return Slot.Head;
                case Slot.LShoulder: return Slot.LUpperArm;
                case Slot.LUpperArm: return Slot.LLowerArm;
                case Slot.LLowerArm: return Slot.LHand;
                case Slot.LHand: return Slot.LMiddle1;
                case Slot.RShoulder: return Slot.RUpperArm;
                case Slot.RUpperArm: return Slot.RLowerArm;
                case Slot.RLowerArm: return Slot.RHand;
                case Slot.RHand: return Slot.RMiddle1;
                case Slot.LUpperLeg: return Slot.LLowerLeg;
                case Slot.LLowerLeg: return Slot.LFoot;
                case Slot.LFoot: return Slot.LToes;
                case Slot.RUpperLeg: return Slot.RLowerLeg;
                case Slot.RLowerLeg: return Slot.RFoot;
                case Slot.RFoot: return Slot.RToes;
            }
            string name = s.ToString();
            if (name.EndsWith("1") || name.EndsWith("2"))
            {
                // Finger joint → next joint of the same finger.
                int n = name[name.Length - 1] - '0';
                if (Enum.TryParse(name.Substring(0, name.Length - 1) + (n + 1), out Slot next))
                {
                    return next;
                }
            }
            return null;
        }
    }
}
