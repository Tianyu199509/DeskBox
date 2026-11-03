using System.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DeskBox.Services;

/// <summary>
/// Offline, zero-bundle-size image text extraction backed by Windows 10/11 built-in
/// <see cref="OcrEngine"/>. Automatically scales oversized long screenshots into
/// <see cref="OcrEngine.MaxImageDimension"/> and joins CJK tokens without artificial spaces.
/// </summary>
public static class ClipboardOcrService
{
    public static bool IsSupported =>
        OcrEngine.AvailableRecognizerLanguages.Count > 0;

    public static async Task<string?> ExtractTextFromImageFileAsync(
        string? imagePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return null;
        }

        OcrEngine? engine = CreateBestOcrEngine();
        if (engine is null)
        {
            return null;
        }

        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(imagePath).AsTask(cancellationToken).ConfigureAwait(false);
            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read).AsTask(cancellationToken).ConfigureAwait(false);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);

            uint width = decoder.PixelWidth;
            uint height = decoder.PixelHeight;
            if (width == 0 || height == 0)
            {
                return null;
            }

            uint maxDim = OcrEngine.MaxImageDimension;
            SoftwareBitmap bitmap;
            if (width > maxDim || height > maxDim)
            {
                double scale = Math.Min(maxDim / (double)width, maxDim / (double)height);
                var transform = new BitmapTransform
                {
                    InterpolationMode = BitmapInterpolationMode.Fant,
                    ScaledWidth = Math.Max(1u, (uint)Math.Floor(width * scale)),
                    ScaledHeight = Math.Max(1u, (uint)Math.Floor(height * scale))
                };

                bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied).AsTask(cancellationToken).ConfigureAwait(false);
            }

            using (bitmap)
            {
                OcrResult? result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
                if (result is null || result.Lines.Count == 0)
                {
                    return null;
                }

                var lines = new List<IReadOnlyList<string>>(result.Lines.Count);
                foreach (OcrLine line in result.Lines)
                {
                    if (line.Words.Count == 0)
                    {
                        continue;
                    }

                    var words = new List<string>(line.Words.Count);
                    foreach (OcrWord word in line.Words)
                    {
                        if (!string.IsNullOrWhiteSpace(word.Text))
                        {
                            words.Add(word.Text.Trim());
                        }
                    }

                    if (words.Count > 0)
                    {
                        lines.Add(words);
                    }
                }

                string formatted = JoinOcrLines(lines);
                return string.IsNullOrWhiteSpace(formatted) ? null : formatted;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    internal static string JoinOcrLines(IReadOnlyList<IReadOnlyList<string>> lines)
    {
        if (lines is null || lines.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (int lineIdx = 0; lineIdx < lines.Count; lineIdx++)
        {
            IReadOnlyList<string> words = lines[lineIdx];
            if (words.Count == 0)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.AppendLine();
            }

            for (int wordIdx = 0; wordIdx < words.Count; wordIdx++)
            {
                string current = words[wordIdx];
                if (string.IsNullOrEmpty(current))
                {
                    continue;
                }

                if (wordIdx > 0 && ShouldInsertSpaceBetweenWords(words[wordIdx - 1], current))
                {
                    sb.Append(' ');
                }

                sb.Append(current);
            }
        }

        return sb.ToString().Trim();
    }

    internal static bool ShouldInsertSpaceBetweenWords(string previousWord, string currentWord)
    {
        if (string.IsNullOrEmpty(previousWord) || string.IsNullOrEmpty(currentWord))
        {
            return false;
        }

        char prevLast = previousWord[^1];
        char currFirst = currentWord[0];

        // Windows OCR splits every CJK / fullwidth character into its own OcrWord;
        // never insert artificial spaces between adjacent CJK characters or fullwidth punctuation.
        if (IsCjkOrFullwidth(prevLast) && IsCjkOrFullwidth(currFirst))
        {
            return false;
        }

        if (IsFullwidthPunctuation(prevLast) || IsFullwidthPunctuation(currFirst))
        {
            return false;
        }

        // Keep tight punctuation attachment for ASCII closing punctuation.
        if (currFirst is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}')
        {
            return false;
        }

        if (prevLast is '(' or '[' or '{' or '/' or '\\')
        {
            return false;
        }

        // Between CJK and ASCII/digits, omit spaces as well for natural Chinese/Japanese copy-paste,
        // while keeping spaces between two Latin/ASCII words.
        if (IsCjkOrFullwidth(prevLast) || IsCjkOrFullwidth(currFirst))
        {
            return false;
        }

        return true;
    }

    internal static bool IsCjkOrFullwidth(char ch) =>
        (ch >= '\u2E80' && ch <= '\u9FFF') ||   // CJK Radicals, Ideographs
        (ch >= '\u3040' && ch <= '\u30FF') ||   // Hiragana, Katakana
        (ch >= '\uAC00' && ch <= '\uD7AF') ||   // Hangul Syllables
        (ch >= '\uF900' && ch <= '\uFAFF') ||   // CJK Compatibility Ideographs
        IsFullwidthPunctuation(ch);

    private static bool IsFullwidthPunctuation(char ch) =>
        (ch >= '\u3000' && ch <= '\u303F') ||   // CJK Symbols and Punctuation
        (ch >= '\uFF00' && ch <= '\uFFEF');     // Halfwidth and Fullwidth Forms

    private static OcrEngine? CreateBestOcrEngine()
    {
        try
        {
            OcrEngine? profileEngine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (profileEngine is not null)
            {
                return profileEngine;
            }

            IReadOnlyList<Language> available = OcrEngine.AvailableRecognizerLanguages;
            if (available.Count > 0)
            {
                return OcrEngine.TryCreateFromLanguage(available[0]);
            }
        }
        catch
        {
            // OCR language pack unavailable or WinRT activation failed.
        }

        return null;
    }
}
