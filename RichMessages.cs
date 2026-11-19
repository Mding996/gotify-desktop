using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace GotifyDesktop;

internal static class MessageActions
{
    internal static readonly Regex UrlPattern = new("https?://[^\\s<>\"']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    private static readonly Regex CodePattern = new(@"(?i)(?:验证码|校验码|动态码|动态密码|登录码|verification\s+code|security\s+code|one[- ]time\s+(?:code|password|passcode)|\bOTP\b)(?:\s|为|是|is|[:：=\-【】\[\]（）()]){0,16}(?<code>(?<!\d)[0-9]{4,8}(?!\d))|(?<before>(?<!\d)[0-9]{4,8}(?!\d))[^\r\n\d]{0,18}(?:为您的验证码|是您的验证码|is\s+your\s+(?:verification\s+code|security\s+code|OTP))", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    internal static Uri? SafeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(char.IsControl)) return null;
        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0 && string.IsNullOrEmpty(uri.UserInfo) ? uri : null;
    }
    internal static string TrimUrl(string value)
    {
        value = value.TrimEnd('.', ',', ';', ':', '!', '?', '。', '，', '；', '！', '？', '）', '】', '》');
        foreach (var pair in new[] { ('(', ')'), ('[', ']'), ('{', '}') })
            while (value.EndsWith(pair.Item2) && value.Count(c => c == pair.Item2) > value.Count(c => c == pair.Item1)) value = value[..^1];
        return value;
    }
    internal static string VerificationCode(GotifyMessage message)
    {
        const int limit = 100000;
        string value = message.Title.Length >= limit ? message.Title[..limit] : string.Concat(message.Title.AsSpan(), "\n".AsSpan(), message.Message.AsSpan(0, Math.Min(message.Message.Length, limit - message.Title.Length - 1)));
        try
        {
            var codes = CodePattern.Matches(value[..Math.Min(value.Length, 100000)]).Select(match => match.Groups["code"].Success ? match.Groups["code"].Value : match.Groups["before"].Value).Distinct().Take(2).ToList();
            return codes.Count == 1 ? codes[0] : "";
        }
        catch (RegexMatchTimeoutException) { return ""; }
    }
    private static string? ExtraString(GotifyMessage message, string space, params string[] path)
    {
        if (message.Extras == null || !message.Extras.TryGetValue(space, out var value)) return null;
        foreach (string key in path) { if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out value)) return null; }
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
    internal static bool IsMarkdown(GotifyMessage message) => ExtraString(message, "client::display", "contentType") == "text/markdown";
    internal static Uri? OriginalUrl(GotifyMessage message) => SafeUrl(ExtraString(message, "client::notification", "click", "url")) ?? BodyUrls(message.Message).FirstOrDefault();
    internal static IEnumerable<Uri> BodyUrls(string value) => UrlPattern.Matches(value[..Math.Min(value.Length, 100000)]).Select(x => SafeUrl(TrimUrl(x.Value))).OfType<Uri>().DistinctBy(x => x.AbsoluteUri);
}

internal static class MessageDocument
{
    private static readonly Regex Tokens = new(@"`[^`\r\n]+`|!?\[[^\]\r\n]*\]\([^)\r\n]+\)|\*\*[^*\r\n]+\*\*|__[^_\r\n]+__|~~[^~\r\n]+~~|\*[^*\r\n]+\*|_[^_\r\n]+_", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    private static readonly Regex HtmlTags = new(@"</?[A-Za-z][A-Za-z0-9]*(?:\s[^>]*|\s*/?)>", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    internal static FlowDocument Create(GotifyMessage message, Action<Uri> open)
    {
        try { return CreateCore(message, open); }
        catch (RegexMatchTimeoutException)
        {
            var fallback = new FlowDocument(new Paragraph(new Run(message.Message[..Math.Min(message.Message.Length, 100000)]))) { PagePadding = new Thickness(0), FontSize = 14 };
            fallback.SetResourceReference(TextElement.ForegroundProperty, "TextBrush"); return fallback;
        }
    }
    private static FlowDocument CreateCore(GotifyMessage message, Action<Uri> open)
    {
        var document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"), FontSize = 14, LineHeight = 21 };
        document.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        string text = message.Message ?? "";
        bool truncated = text.Length > 100000;
        if (truncated) text = text[..100000];
        bool markdown = MessageActions.IsMarkdown(message);
        string? fence = null; var code = new StringBuilder();
        void CodeBlock()
        {
            var block = new Paragraph(new Run(code.ToString().TrimEnd('\n'))) { FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 13, Padding = new Thickness(9), Margin = new Thickness(0, 4, 0, 10) };
            block.SetResourceReference(TextElement.BackgroundProperty, "RaisedBrush"); document.Blocks.Add(block); code.Clear();
        }
        foreach (string original in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = original;
            if (markdown && fence != null)
            {
                if (line.TrimStart().StartsWith(fence)) { CodeBlock(); fence = null; } else code.AppendLine(line);
                continue;
            }
            if (markdown && (line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("~~~"))) { fence = line.TrimStart()[..3]; continue; }
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, markdown ? 7 : 0) };
            if (markdown)
            {
                var heading = Regex.Match(line, @"^(#{1,6})\s+(.+?)\s*#*$");
                var bullet = Regex.Match(line, @"^\s*([-+*]|\d+[.)])\s+(.+)$");
                if (heading.Success) { paragraph.FontSize = 27 - heading.Groups[1].Length * 2; paragraph.FontWeight = FontWeights.SemiBold; line = heading.Groups[2].Value; }
                else if (bullet.Success) { paragraph.Margin = new Thickness(18, 0, 0, 5); paragraph.TextIndent = -14; paragraph.Inlines.Add(new Run(char.IsDigit(bullet.Groups[1].Value[0]) ? bullet.Groups[1].Value + " " : "•  ")); line = bullet.Groups[2].Value; }
                else if (line.TrimStart().StartsWith('>')) { paragraph.Margin = new Thickness(4, 2, 0, 8); paragraph.BorderThickness = new Thickness(3, 0, 0, 0); paragraph.Padding = new Thickness(9, 0, 0, 0); paragraph.SetResourceReference(Block.BorderBrushProperty, "LineBrush"); line = Regex.Replace(line, @"^\s*>\s?", ""); }
            }
            Append(paragraph.Inlines, line, markdown, open, 0); document.Blocks.Add(paragraph);
        }
        if (fence != null) CodeBlock();
        if (truncated) document.Blocks.Add(new Paragraph(new Run("内容较长，可切换“原文”查看全部。")));
        return document;
    }
    private static void Append(InlineCollection target, string text, bool markdown, Action<Uri> open, int depth)
    {
        if (!markdown || depth >= 4) { Plain(target, text, markdown, open); return; }
        int offset = 0;
        foreach (Match match in Tokens.Matches(text))
        {
            Plain(target, text[offset..match.Index], true, open); string token = match.Value;
            if (token.StartsWith('`'))
            {
                var run = new Run(token[1..^1]) { FontFamily = new System.Windows.Media.FontFamily("Consolas") }; run.SetResourceReference(TextElement.BackgroundProperty, "RaisedBrush"); target.Add(run);
            }
            else if (token.StartsWith('[') || token.StartsWith("!["))
            {
                int end = token.IndexOf("](", StringComparison.Ordinal); string label = token[(token[0] == '!' ? 2 : 1)..end];
                var uri = MessageActions.SafeUrl(token[(end + 2)..^1]);
                if (uri != null) Link(target, (token[0] == '!' ? "图片：" : "") + (label.Length == 0 ? uri.Host : label), uri, open);
                else target.Add(new Run(label));
            }
            else
            {
                bool wide = token.StartsWith("**") || token.StartsWith("__") || token.StartsWith("~~");
                var span = new Span();
                if (token.StartsWith("~~")) span.TextDecorations = TextDecorations.Strikethrough;
                else if (wide) span.FontWeight = FontWeights.Bold;
                else span.FontStyle = FontStyles.Italic;
                Append(span.Inlines, token[(wide ? 2 : 1)..^(wide ? 2 : 1)], true, open, depth + 1); target.Add(span);
            }
            offset = match.Index + match.Length;
        }
        Plain(target, text[offset..], true, open);
    }
    private static void Plain(InlineCollection target, string text, bool stripHtml, Action<Uri> open)
    {
        if (stripHtml) text = WebUtility.HtmlDecode(HtmlTags.Replace(text, ""));
        int offset = 0;
        foreach (Match match in MessageActions.UrlPattern.Matches(text))
        {
            string value = MessageActions.TrimUrl(match.Value); var uri = MessageActions.SafeUrl(value);
            if (uri == null) continue;
            target.Add(new Run(text[offset..match.Index])); Link(target, value, uri, open); offset = match.Index + value.Length;
        }
        target.Add(new Run(text[offset..]));
    }
    private static void Link(InlineCollection target, string label, Uri uri, Action<Uri> open)
    {
        var link = new Hyperlink(new Run(label)) { NavigateUri = uri, ToolTip = uri.AbsoluteUri };
        link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
        link.RequestNavigate += (_, e) => { open(e.Uri); e.Handled = true; }; target.Add(link);
    }
}

public partial class MainWindow
{
    private Uri? _originalUrl;
    private string _verificationCode = "";
    private GotifyMessage? _renderedMessage;
    private string? _renderedBody;
    private Dictionary<string, JsonElement>? _renderedExtras;
    internal Action<string>? CopyCodeSink { get; set; }
    internal Action<Uri>? OpenUrlSink { get; set; }
    private void UpdateRichDetails(GotifyMessage message)
    {
        if (_readerSuspended) return;
        _verificationCode = message.VerificationCode; _originalUrl = MessageActions.OriginalUrl(message);
        VerificationCard.Visibility = _verificationCode.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        VerificationCodeBox.Text = _verificationCode;
        CopyCodeButton.Visibility = _verificationCode.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CopyCodeButton.ToolTip = "仅复制识别到的验证码：" + _verificationCode;
        OpenOriginalButton.Visibility = _originalUrl == null ? Visibility.Collapsed : Visibility.Visible;
        OpenOriginalButton.ToolTip = _originalUrl?.AbsoluteUri;
        if (ShowSourceBox.IsChecked != true && (!ReferenceEquals(_renderedMessage, message) || !ReferenceEquals(_renderedBody, message.Message) || !ReferenceEquals(_renderedExtras, message.Extras)))
        {
            DetailRichBody.Document = MessageDocument.Create(message, OpenMessageUrl);
            DetailRichBody.Height = 80;
            _renderedMessage = message; _renderedBody = message.Message; _renderedExtras = message.Extras;
        }
        DetailRichBody.Visibility = ShowSourceBox.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        DetailBody.Visibility = ShowSourceBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ResizeRichReader(object? sender, EventArgs e)
    {
        if (!DetailRichBody.IsVisible) return;
        // ExtentHeight can equal the viewport even for a short document. Adding padding
        // to that value on every layout grows the reader repeatedly and reformats text.
        if (DetailRichBody.ExtentHeight <= DetailRichBody.ViewportHeight + 0.5) return;
        double height = Math.Clamp(Math.Ceiling(DetailRichBody.ExtentHeight) + 4, 80, 360);
        if (height > DetailRichBody.Height + 0.5) DetailRichBody.Height = height;
    }
    private void ShowSource_Changed(object sender, RoutedEventArgs e) { if (_selectedMessage != null) UpdateRichDetails(_selectedMessage); }
    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        CopyVerificationCode(_verificationCode);
    }
    private void CopyListCode_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is System.Windows.Controls.Button { DataContext: GotifyMessage message })
            CopyVerificationCode(message.VerificationCode);
    }
    private void CopyVerificationCode(string code)
    {
        if (code.Length == 0) return;
        try { if (CopyCodeSink != null) CopyCodeSink(code); else System.Windows.Clipboard.SetText(code); SetNotice("验证码已复制。", NoticeTone.Success, transient: true); }
        catch { SetNotice("剪贴板暂不可用，请重试。", NoticeTone.Warning); }
    }
    private void OpenOriginal_Click(object sender, RoutedEventArgs e) { if (_originalUrl != null) OpenMessageUrl(_originalUrl); }
    private void OpenMessageUrl(Uri uri)
    {
        if (MessageActions.SafeUrl(uri.AbsoluteUri) is not Uri safe) return;
        if (OpenUrlSink != null) { OpenUrlSink(safe); return; }
        try { Process.Start(new ProcessStartInfo(safe.AbsoluteUri) { UseShellExecute = true }); }
        catch { SetNotice("无法打开链接，请复制消息后手动打开。", NoticeTone.Warning); }
    }
}
