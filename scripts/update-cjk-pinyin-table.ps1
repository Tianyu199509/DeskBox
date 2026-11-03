[CmdletBinding()]
param([switch]$Check)

$ErrorActionPreference = 'Stop'

if ([System.Threading.Thread]::CurrentThread.GetApartmentState() -ne [System.Threading.ApartmentState]::STA) {
    $forwardArgs = @('-NoProfile', '-Sta', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    if ($Check) { $forwardArgs += '-Check' }
    & pwsh @forwardArgs
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
    return
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputPath = Join-Path $repoRoot 'src/DeskBox/Services/SettingsSearchMatcher.PinyinData.cs'

Add-Type -TypeDefinition @"
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

public static class MsImeCjkPinyinExtractor
{
    [ComImport]
    [Guid("019F7152-E6DB-11d0-83C3-00C04FDDB82E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFELanguage
    {
        int Open();
        int Close();
        int GetJMorphResult(
            uint dwRequest,
            uint dwCMode,
            int cwchInput,
            [MarshalAs(UnmanagedType.LPWStr)] string pwchInput,
            IntPtr pfCInfo,
            out IntPtr ppResult);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct MORRSLT
    {
        public uint dwSize;
        public IntPtr pwchOutput;
        public ushort cchOutput;
        public IntPtr pwchRead;
        public ushort cchRead;
    }

    private const uint FELANG_REQ_REV = 0x00030000;
    private const uint FELANG_CMODE_PINYIN = 0x00000100;
    private const uint FELANG_CMODE_NOINVISIBLECHAR = 0x40000000;

    public static string?[] ExtractRange(int startCodePoint, int endCodePoint)
    {
        int count = endCodePoint - startCodePoint + 1;
        string?[] result = new string?[count];

        Type? imeType = Type.GetTypeFromProgID("MSIME.China");
        if (imeType == null)
        {
            throw new InvalidOperationException("MSIME.China COM component is not registered on this machine.");
        }

        object? instance = Activator.CreateInstance(imeType);
        IFELanguage feLang = (IFELanguage)instance!;
        feLang.Open();
        try
        {
            for (int cp = startCodePoint; cp <= endCodePoint; cp++)
            {
                string input = ((char)cp).ToString();
                IntPtr ptr = IntPtr.Zero;
                int hr = feLang.GetJMorphResult(
                    FELANG_REQ_REV,
                    FELANG_CMODE_PINYIN | FELANG_CMODE_NOINVISIBLECHAR,
                    1,
                    input,
                    IntPtr.Zero,
                    out ptr);
                if (hr == 0 && ptr != IntPtr.Zero)
                {
                    try
                    {
                        MORRSLT mor = Marshal.PtrToStructure<MORRSLT>(ptr);
                        if (mor.pwchOutput != IntPtr.Zero && mor.cchOutput > 0)
                        {
                            string raw = Marshal.PtrToStringUni(mor.pwchOutput, mor.cchOutput) ?? string.Empty;
                            int nulIndex = raw.IndexOf('\0');
                            if (nulIndex >= 0)
                            {
                                raw = raw.Substring(0, nulIndex);
                            }

                            string ascii = NormalizeSyllable(raw);
                            if (ascii.Length > 0)
                            {
                                result[cp - startCodePoint] = ascii;
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(ptr);
                    }
                }
            }
        }
        finally
        {
            feLang.Close();
            Marshal.ReleaseComObject(feLang);
        }

        return result;
    }

    private static string NormalizeSyllable(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var pre = new StringBuilder(raw.Length);
        for (int index = 0; index < raw.Length; index++)
        {
            char ch = raw[index];
            if ("\u00fc\u00dc\u01d6\u01d8\u01da\u01dc\u01d5\u01d7\u01d9\u01db".IndexOf(ch) >= 0)
            {
                pre.Append('v');
            }
            else
            {
                pre.Append(ch);
            }
        }

        string normalized = pre.ToString().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        for (int index = 0; index < normalized.Length; index++)
        {
            char ch = normalized[index];
            if (ch == '\u0308' && sb.Length > 0 && sb[sb.Length - 1] == 'u')
            {
                sb[sb.Length - 1] = 'v';
                continue;
            }

            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            char lower = char.ToLowerInvariant(ch);
            if (lower == '\'' || lower == ' ' || lower == ',' || lower == '/')
            {
                break;
            }

            if (lower >= 'a' && lower <= 'z')
            {
                sb.Append(lower);
            }
        }

        string syllable = sb.ToString();
        if (syllable.StartsWith("jv", StringComparison.Ordinal) ||
            syllable.StartsWith("qv", StringComparison.Ordinal) ||
            syllable.StartsWith("xv", StringComparison.Ordinal) ||
            syllable.StartsWith("yv", StringComparison.Ordinal))
        {
            syllable = string.Concat(syllable.AsSpan(0, 1), "u", syllable.AsSpan(2));
        }

        return syllable;
    }
}
"@

$startCp = 0x4E00
$endCp = 0x9FFF
$totalCount = $endCp - $startCp + 1
$readings = [MsImeCjkPinyinExtractor]::ExtractRange($startCp, $endCp)

# Unicode Unihan kMandarin supplements for U+9FA2..U+9FFF and CJK metric ideographs not in MSIME.China's dictionary.
$unihanSupplements = [ordered]@{
    0x5301 = 'wen';  0x74E7 = 'shi';  0x74E9 = 'qian'; 0x74F0 = 'fen';  0x74F1 = 'mao'
    0x74F2 = 'dun';  0x74F8 = 'bai';  0x74FC = 'li';   0x7505 = 'li'
    0x9FA2 = 'he';   0x9FA3 = 'jiao'; 0x9FA4 = 'xie';  0x9FA5 = 'yu'
    0x9FA6 = 'tang'; 0x9FA7 = 'shu';  0x9FA8 = 'hui';  0x9FA9 = 'qian'; 0x9FAA = 'nao'
    0x9FAB = 'gan';  0x9FAC = 'gang'; 0x9FAD = 'yan';  0x9FAE = 'ju';   0x9FAF = 'huo'
    0x9FB0 = 'ju';   0x9FB1 = 'wang'; 0x9FB2 = 'chi';  0x9FB3 = 'yi';   0x9FB4 = 'yu'
    0x9FB5 = 'shou'; 0x9FB6 = 'qing'; 0x9FB7 = 'gong'; 0x9FB8 = 'chang'; 0x9FB9 = 'juan'
    0x9FBA = 'gan';  0x9FBB = 'xue';  0x9FBC = 'bi';   0x9FBD = 'zha';  0x9FBE = 'ge'
    0x9FBF = 'cao';  0x9FC0 = 'ao';   0x9FC1 = 'yi';   0x9FC2 = 'lu';   0x9FC3 = 'mai'
    0x9FC4 = 'lu';   0x9FC5 = 'yu';   0x9FC6 = 'fu';   0x9FC7 = 'jing'; 0x9FC8 = 'ga'
    0x9FC9 = 'nian'; 0x9FCA = 'lai';  0x9FCB = 'song'; 0x9FCC = 'yuan'; 0x9FCD = 'gang'
    0x9FCE = 'tuo';  0x9FCF = 'mai';  0x9FD0 = 'jing'; 0x9FD1 = 'qi';   0x9FD2 = 'man'
    0x9FD3 = 'kui';  0x9FD4 = 'ge';   0x9FD5 = 'da';   0x9FD6 = 'xi';   0x9FD7 = 'wei'
    0x9FD8 = 'chang'; 0x9FD9 = 'tao'; 0x9FDA = 'gan';  0x9FDB = 'fei';  0x9FDC = 'ze'
    0x9FDD = 'shen'; 0x9FDE = 'yi';   0x9FDF = 'zha';  0x9FE0 = 'xun';  0x9FE1 = 'mian'
    0x9FE2 = 'wang'; 0x9FE3 = 'sha';  0x9FE4 = 'di';   0x9FE5 = 'zhu';  0x9FE6 = 'ceng'
    0x9FE7 = 'yang'; 0x9FE8 = 'wang'; 0x9FE9 = 'quan'; 0x9FEA = 'dang'; 0x9FEB = 'ao'
    0x9FEC = 'tian'; 0x9FED = 'ni';   0x9FEE = 'ji';   0x9FEF = 'hao';  0x9FF0 = 'yu'
    0x9FF1 = 'fang'; 0x9FF2 = 'ge';   0x9FF3 = 'an';   0x9FF4 = 'cha';  0x9FF5 = 'wan'
    0x9FF6 = 'zhou'; 0x9FF7 = 'lin';  0x9FF8 = 'kuang'; 0x9FF9 = 'na';  0x9FFA = 'ao'
    0x9FFB = 'diao'; 0x9FFC = 'zhou'; 0x9FFD = 'nie';  0x9FFE = 'bi';   0x9FFF = 'cha'
}

foreach ($entry in $unihanSupplements.GetEnumerator()) {
    $idx = [int]$entry.Key - $startCp
    if ([string]::IsNullOrEmpty($readings[$idx])) {
        $readings[$idx] = [string]$entry.Value
    }
}

# Curated multi-reading Mandarin polyphones (Simplified + Traditional).
# First reading becomes the primary syllable in CjkPrimarySyllableMap; remaining readings go into PolyphoneAlternates.
$polyphoneSpecs = @(
    @('重', 'chong', 'zhong'),
    @('行', 'hang', 'xing'),
    @('调', 'tiao', 'diao'),
    @('調', 'tiao', 'diao'),
    @('弹', 'tan', 'dan'),
    @('彈', 'tan', 'dan'),
    @('还', 'huan', 'hai'),
    @('還', 'huan', 'hai'),
    @('长', 'chang', 'zhang'),
    @('長', 'chang', 'zhang'),
    @('藏', 'cang', 'zang'),
    @('省', 'sheng', 'xing'),
    @('率', 'lv', 'shuai'),
    @('角', 'jiao', 'jue'),
    @('校', 'jiao', 'xiao'),
    @('降', 'jiang', 'xiang'),
    @('宿', 'su', 'xiu'),
    @('传', 'chuan', 'zhuan'),
    @('傳', 'chuan', 'zhuan'),
    @('参', 'can', 'cen', 'shen'),
    @('參', 'can', 'cen', 'shen'),
    @('差', 'cha', 'chai', 'ci'),
    @('强', 'qiang', 'jiang'),
    @('強', 'qiang', 'jiang'),
    @('折', 'zhe', 'she'),
    @('模', 'mo', 'mu'),
    @('系', 'xi', 'ji'),
    @('繫', 'xi', 'ji'),
    @('转', 'zhuan', 'zhuai'),
    @('轉', 'zhuan', 'zhuai'),
    @('单', 'dan', 'shan', 'chan'),
    @('單', 'dan', 'shan', 'chan'),
    @('便', 'bian', 'pian'),
    @('盛', 'sheng', 'cheng'),
    @('着', 'zhuo', 'zhao', 'zhe'),
    @('著', 'zhu', 'zhuo', 'zhao', 'zhe'),
    @('数', 'shu', 'shuo'),
    @('數', 'shu', 'shuo'),
    @('度', 'du', 'duo'),
    @('缩', 'suo', 'su'),
    @('縮', 'suo', 'su'),
    @('卡', 'ka', 'qia'),
    @('奇', 'qi', 'ji'),
    @('薄', 'bo', 'bao'),
    @('露', 'lu', 'lou'),
    @('塞', 'sai', 'se'),
    @('屏', 'ping', 'bing'),
    @('朝', 'chao', 'zhao'),
    @('乐', 'le', 'yue'),
    @('樂', 'le', 'yue'),
    @('称', 'cheng', 'chen'),
    @('稱', 'cheng', 'chen'),
    @('圈', 'quan', 'juan'),
    @('没', 'mei', 'mo'),
    @('沒', 'mei', 'mo'),
    @('得', 'de', 'dei'),
    @('地', 'di', 'de'),
    @('都', 'dou', 'du'),
    @('给', 'gei', 'ji'),
    @('給', 'gei', 'ji'),
    @('壳', 'ke', 'qiao'),
    @('殼', 'ke', 'qiao'),
    @('削', 'xue', 'xiao'),
    @('读', 'du', 'dou'),
    @('讀', 'du', 'dou'),
    @('解', 'jie', 'xie'),
    @('提', 'ti', 'di'),
    @('抹', 'mo', 'ma'),
    @('拓', 'tuo', 'ta'),
    @('识', 'shi', 'zhi'),
    @('識', 'shi', 'zhi'),
    @('曝', 'pu', 'bao'),
    @('粘', 'zhan', 'nian'),
    @('秘', 'mi', 'bi'),
    @('钥', 'yao', 'yue'),
    @('鑰', 'yao', 'yue'),
    @('栅', 'zha', 'shan'),
    @('柵', 'zha', 'shan'),
    @('阈', 'yu', 'fa'),
    @('閾', 'yu', 'fa'),
    @('属', 'shu', 'zhu'),
    @('屬', 'shu', 'zhu'),
    @('帧', 'zhen', 'zheng'),
    @('幀', 'zhen', 'zheng'),
    @('色', 'se', 'shai'),
    @('熟', 'shu', 'shou'),
    @('血', 'xue', 'xie'),
    @('纤', 'xian', 'qian'),
    @('纖', 'xian', 'qian'),
    @('劲', 'jin', 'jing'),
    @('勁', 'jin', 'jing'),
    @('刹', 'sha', 'cha'),
    @('剎', 'sha', 'cha'),
    @('伺', 'si', 'ci'),
    @('否', 'fou', 'pi'),
    @('伽', 'jia', 'ga', 'qie'),
    @('咖', 'ka', 'ga'),
    @('埋', 'mai', 'man'),
    @('脉', 'mai', 'mo'),
    @('脈', 'mai', 'mo'),
    @('瀑', 'pu', 'bao'),
    @('厦', 'sha', 'xia'),
    @('廈', 'sha', 'xia'),
    @('什', 'shen', 'shi'),
    @('似', 'si', 'shi'),
    @('吓', 'xia', 'he'),
    @('嚇', 'xia', 'he'),
    @('咽', 'yan', 'ye'),
    @('扎', 'zha', 'za'),
    @('轴', 'zhou', 'zhu'),
    @('軸', 'zhou', 'zhu'),
    @('恶', 'e', 'wu'),
    @('惡', 'e', 'wu'),
    @('阿', 'a', 'e'),
    @('扁', 'bian', 'pian'),
    @('大', 'da', 'dai'),
    @('叶', 'ye', 'xie'),
    @('葉', 'ye', 'xie'),
    @('尺', 'chi', 'che')
)

$polyphoneMap = [System.Collections.Generic.SortedDictionary[uint16, string[]]]::new()
foreach ($spec in $polyphoneSpecs) {
    $ch = [char]$spec[0]
    $cp = [uint16]$ch
    $primary = [string]$spec[1]
    $readings[[int]$cp - $startCp] = $primary
    $alts = @($spec[2..($spec.Length - 1)] | ForEach-Object { [string]$_ })
    $polyphoneMap[$cp] = $alts
}

$paletteSet = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
for ($i = 0; $i -lt $totalCount; $i++) {
    $s = $readings[$i]
    if (-not [string]::IsNullOrEmpty($s)) {
        [void]$paletteSet.Add($s)
    }
}
foreach ($alts in $polyphoneMap.Values) {
    foreach ($alt in $alts) {
        [void]$paletteSet.Add($alt)
    }
}

$palette = [string[]]::new($paletteSet.Count)
$paletteSet.CopyTo($palette)
$paletteIndex = [System.Collections.Generic.Dictionary[string, int]]::new([System.StringComparer]::Ordinal)
for ($i = 0; $i -lt $palette.Length; $i++) {
    $paletteIndex[$palette[$i]] = $i + 1 # 1-based; 0 = unmapped
}

$mappedCount = 0
$rawBytes = [byte[]]::new($totalCount * 2)
for ($i = 0; $i -lt $totalCount; $i++) {
    $s = $readings[$i]
    $code = 0
    if (-not [string]::IsNullOrEmpty($s)) {
        $code = $paletteIndex[$s]
        $mappedCount++
    }
    $rawBytes[$i * 2] = [byte]($code -band 0xFF)
    $rawBytes[$i * 2 + 1] = [byte](($code -shr 8) -band 0xFF)
}

$lines = [System.Collections.Generic.List[string]]::new(2200)
$lines.Add('// Generated by scripts/update-cjk-pinyin-table.ps1. Do not edit by hand.')
$lines.Add('// Covers Unicode CJK Unified Ideographs U+4E00..U+9FFF (20,992 code points) via PE .rdata RVA static bytes.')
$lines.Add('namespace DeskBox.Services;')
$lines.Add('')
$lines.Add('internal static partial class SettingsSearchMatcher')
$lines.Add('{')
$lines.Add('    internal const int CjkUnifiedStart = 0x4E00;')
$lines.Add('    internal const int CjkUnifiedEnd = 0x9FFF;')
$lines.Add("    internal const int CjkUnifiedCount = $totalCount;")
$lines.Add("    internal const int MappedCjkCount = $mappedCount;")
$lines.Add('')
$lines.Add('    private static readonly string[] SyllablePalette =')
$lines.Add('    [')
for ($i = 0; $i -lt $palette.Length; $i += 12) {
    $end = [Math]::Min($i + 11, $palette.Length - 1)
    $row = ($palette[$i..$end] | ForEach-Object { '"' + $_ + '"' }) -join ', '
    $lines.Add("        $row,")
}
$lines.Add('    ];')
$lines.Add('')
$lines.Add('    private static readonly ushort[] PolyphoneCodePoints =')
$lines.Add('    [')
$polyKeys = [uint16[]]::new($polyphoneMap.Count)
$polyphoneMap.Keys.CopyTo($polyKeys, 0)
for ($i = 0; $i -lt $polyKeys.Length; $i += 12) {
    $end = [Math]::Min($i + 11, $polyKeys.Length - 1)
    $row = ($polyKeys[$i..$end] | ForEach-Object { '0x{0:X4}' -f $_ }) -join ', '
    $lines.Add("        $row,")
}
$lines.Add('    ];')
$lines.Add('')
$lines.Add('    private static readonly string[][] PolyphoneAlternates =')
$lines.Add('    [')
foreach ($cp in $polyKeys) {
    $alts = ($polyphoneMap[$cp] | ForEach-Object { '"' + $_ + '"' }) -join ', '
    $lines.Add(("        [{0}], // U+{1:X4} '{2}'" -f $alts, $cp, [char]$cp))
}
$lines.Add('    ];')
$lines.Add('')
$lines.Add('    // 20,992 little-endian uint16 entries (0 = unmapped, 1..N = SyllablePalette[entry - 1]).')
$lines.Add('    private static ReadOnlySpan<byte> CjkPrimarySyllableMap =>')
$lines.Add('    [')
for ($i = 0; $i -lt $rawBytes.Length; $i += 24) {
    $end = [Math]::Min($i + 23, $rawBytes.Length - 1)
    $sb = [System.Text.StringBuilder]::new(160)
    [void]$sb.Append('        ')
    for ($j = $i; $j -le $end; $j++) {
        if ($j -gt $i) { [void]$sb.Append(', ') }
        [void]$sb.Append(('0x{0:X2}' -f $rawBytes[$j]))
    }
    [void]$sb.Append(',')
    $lines.Add($sb.ToString())
}
$lines.Add('    ];')
$lines.Add('}')

$text = ($lines -join "`r`n") + "`r`n"
if ($Check) {
    if (-not (Test-Path -LiteralPath $outputPath) -or
        [System.IO.File]::ReadAllText($outputPath, [System.Text.Encoding]::UTF8).Replace("`r`n", "`n") -cne $text.Replace("`r`n", "`n")) {
        throw 'CJK Pinyin table is stale. Run scripts/update-cjk-pinyin-table.ps1.'
    }
    Write-Output "CJK Pinyin table matches ($mappedCount / $totalCount code points mapped, $($polyKeys.Length) polyphones)."
} else {
    [System.IO.File]::WriteAllText($outputPath, $text, [System.Text.UTF8Encoding]::new($false))
    Write-Output "Updated $outputPath ($mappedCount / $totalCount code points mapped, $($palette.Length) syllables, $($polyKeys.Length) polyphones)."
}
