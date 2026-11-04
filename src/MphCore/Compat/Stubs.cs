// Stubs for MphRead types the mod doesn't use (its OpenGL scene + entity system).
// The readers reference them in signatures only; the mod never calls those paths.
using System;
using System.Collections.Generic;
using System.Text;
using OpenTK.Mathematics;
namespace MphRead
{
    public class ProgramException : Exception { public ProgramException(string message) : base(message) { } }
    public class Scene { }
    public class EntityBase
    {
        public static Matrix4 GetTransformMatrix(Vector3 facing, Vector3 up)
        {
            Vector3 right = Vector3.Cross(up, facing).Normalized();
            up = Vector3.Cross(facing, right);
            Matrix4 transform = default;
            transform.M11 = right.X;
            transform.M12 = right.Y;
            transform.M13 = right.Z;
            transform.M14 = 0;
            transform.M21 = up.X;
            transform.M22 = up.Y;
            transform.M23 = up.Z;
            transform.M24 = 0;
            transform.M31 = facing.X;
            transform.M32 = facing.Y;
            transform.M33 = facing.Z;
            transform.M34 = 0;
            transform.M41 = 0;
            transform.M42 = 0;
            transform.M43 = 0;
            transform.M44 = 1;
            return transform;
        }

        public static Matrix4 GetTransformMatrix(Vector3 facing, Vector3 up, Vector3 position)
        {
            Vector3 right = Vector3.Cross(up, facing).Normalized();
            up = Vector3.Cross(facing, right);
            Matrix4 transform = default;
            transform.M11 = right.X;
            transform.M12 = right.Y;
            transform.M13 = right.Z;
            transform.M14 = 0;
            transform.M21 = up.X;
            transform.M22 = up.Y;
            transform.M23 = up.Z;
            transform.M24 = 0;
            transform.M31 = facing.X;
            transform.M32 = facing.Y;
            transform.M33 = facing.Z;
            transform.M34 = 0;
            transform.M41 = position.X;
            transform.M42 = position.Y;
            transform.M43 = position.Z;
            transform.M44 = 1;
            return transform;
        }
    }
    public class DynamicLightEntityBase : EntityBase { }
}
namespace MphRead.Entities
{
    public class BeamProjectileEntity { }
}
namespace MphRead.Formats.Culling
{
    public struct NodeRef
    {
        public int PartIndex; public int NodeIndex; public int ModelIndex;
        public static readonly NodeRef None = new NodeRef { PartIndex = -1, NodeIndex = -1, ModelIndex = -1 };
    }
}
namespace MphRead.Text
{
    public static class Strings
    {
        private static readonly IReadOnlyList<string> _nonAscii = new string[]
        {
            "€", " ", "‚", "ƒ", "„", "…", "†", "‡", "ˆ", "‰", "Š", "‹", "Œ", " ", "Ž", " ", " ", "‘", "’", "“", "”", "•", "–", "—", "˜", "™",
            "š", "›", "œ", " ", "ž", "Ÿ", " ", "¡", "¢", "£", "¤", "¥", "¦", "§", "¨", "©", "ª", "«", "¬", "–", "®", "¯", "°", "±", "²", "³",
            "´", "µ", "¶", "·", "¸", "¹", "º", "»", "¼", "½", "¾", "¿", "À", "Á", "Â", "Ã", "Ä", "Å", "Æ", "Ç", "È", "É", "Ê", "Ë", "Ì", "Í",
            "Î", "Ï", "Ð", "Ñ", "Ò", "Ó", "Ô", "Õ", "Ö", "×", "Ø", "Ù", "Ú", "Û", "Ü", "Ý", "Þ", "ß", "à", "á", "â", "ã", "ä", "å", "æ", "ç",
            "è", "é", "ê", "ë", "ì", "í", "î", "ï", "ð", "ñ", "ò", "ó", "ô", "õ", "ö", "÷", "ø", "ù", "ú", "û", "ü", "ý", "þ", "ÿ", " ",
            "ぁ", "あ", "ぃ", "い", "ぅ", "う", "ぇ", "え", "ぉ", "お", "か", "が", "き", "ぎ", "く", "ぐ", "け", "げ", "こ", "ご", "さ", "ざ",
            "し", "じ", "す", "ず", "せ", "ぜ", "そ", "ぞ", "た", "だ", "ち", "ぢ", "っ", "つ", "づ", "て", "で", "と", "ど", "な", "に", "ぬ",
            "ね", "の", "は", "ば", "ぱ", "ひ", "び", "ぴ", "ふ", "ぶ", "ぷ", "へ", "べ", "ぺ", "ほ", "ぼ", "ぽ", "ま", "み", "む", "め", "も",
            "ゃ", "や", "ゅ", "ゆ", "ょ", "よ", "ら", "り", "る", "れ", "ろ", "ゎ", "わ", "ゐ", "ゑ", "を", "ん", "ァ", "ア", "ィ", "イ", "ゥ",
            "ウ", "ェ", "エ", "ォ", "オ", "カ", "ガ", "キ", "ギ", "ク", "グ", "ケ", "ゲ", "コ", "ゴ", "サ", "ザ", "シ", "ジ", "ス", "ズ", "セ",
            "ゼ", "ソ", "ゾ", "タ", "ダ", "チ", "ヂ", "ッ", "ツ", "ヅ", "テ", "デ", "ト", "ド", "ナ", "ニ", "ヌ", "ネ", "ノ", "ハ", "バ", "パ",
            "ヒ", "ビ", "ピ", "フ", "ブ", "プ", "ヘ", "ベ", "ペ", "ホ", "ボ", "ポ", "マ", "ミ", "ム", "メ", "モ", "ャ", "ヤ", "ュ", "ユ", "ョ",
            "ヨ", "ラ", "リ", "ル", "レ", "ロ", "ヮ", "ワ", "ヰ", "ヱ", "ヲ", "ン", "ヴ", "ヵ", "ㇰ", " ", " ", " ", " ", " ", " ", " ",
            "、", "。", "'", "・", "・", ":", ";", "?", "!", "゛", "゜", "´", "`", "¨", "^", "‾", "_", " ", " ", "ゝ", "ゞ", " ", " ",
            "々", " ", " ", "–", "—", "−", "／", "＼", "˜", " ", "|", "…", " ", "'", "'", "\"", "\"", "(", ")", "(", ")", "[", "]", "{", "}",
            "<", ">", " ", " ", "「", "」", " ", " ", " ", " ", "+", "-", "±", "×", "÷", "=", " ", " ", " ", " ", " ", "∞", "∴", " ", " ",
            "°", "ᐟ", "ᐥ ", " ", " ", " ", " ", " ", " ", " ", " ", " ", " ", " ", " ", " ", " ", " ", " ", " ", " ", " ", " ",
            " ", " ", " ", " ", " ", " ", " ", " ", " ", " ", " ", " ", " ", " ", " "
        };
        public static string ReplaceNonAscii(string value)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if ((c & 0xA0) == 0xA0)
                {
                    return "<kanji>";
                }
                if ((c & 0x80) == 0)
                {
                    sb.Append(c);
                }
                else
                {
                    int index = value[++i] & 0x3F | ((c & 0x1F) << 6);
                    if (index >= 128 && index - 128 <= _nonAscii.Count)
                    {
                        sb.Append(_nonAscii[index - 128]);
                    }
                    else
                    {
                        sb.Append((char)index);
                    }
                }
            }
            return sb.ToString();
        }
    }
}
namespace MphRead.Hud { internal static class _Empty { } }
namespace MphRead.Utility { internal static class _Empty { } }
namespace MphRead.Export { internal static class _Empty { } }
namespace MphRead.Sound
{
    public static class Sfx
    {
        public static float CalculatePitchDiv(float pitchFac)
        {
            if (pitchFac == 0)
            {
                pitchFac = 1;
            }
            int pitchInt = (int)pitchFac;
            if (pitchFac <= 0xFFF)
            {
                pitchInt = -((0x600000 / pitchInt) >> 1);
            }
            else if (pitchFac <= 0x1FFF)
            {
                pitchInt = (768 * (pitchInt - 0x2000)) >> 12;
            }
            else
            {
                pitchInt = (768 * (pitchInt - 0x2000)) >> 13;
            }
            float semitones = pitchInt / 64f;
            float octaves = MathF.Abs(semitones / 12f);
            if (semitones >= 0)
            {
                return MathF.Pow(2, octaves);
            }
            return MathF.Pow(0.5f, octaves);
        }
    }
}
namespace MphRead.Effects { internal static class _Empty { } }
