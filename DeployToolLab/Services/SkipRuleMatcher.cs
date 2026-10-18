using System.Text.RegularExpressions;
using DeployTool.Models;

namespace DeployTool.Services;

/// <summary>
/// 스킵 규칙 패턴 매칭.
/// - "Logs/"        : 경로 중 해당 이름의 폴더 아래 모든 파일
/// - "*.pdb"        : 파일명 glob (* ?)
/// - "Web/web.config", "Config/*.json" : 상대 경로 끝부분 glob
/// </summary>
public static class SkipRuleMatcher
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>
    /// 스캔 직후 기본 결정을 지정합니다. 변경 파일은 기본 Apply,
    /// 패턴에 매칭되는 파일은 Skip으로 지정하고 매칭 개수를 반환합니다.
    /// </summary>
    public static int Apply(IEnumerable<FileChangeItem> items, IEnumerable<string> patterns)
    {
        var rules = patterns
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(p => new Rule(p))
            .ToList();

        var count = 0;
        foreach (var item in items)
        {
            item.SkipRule = null;
            if (item.Status == FileChangeStatus.Identical) continue;

            var parts = item.RelativePath.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            var rule = rules.FirstOrDefault(r => r.IsMatch(parts));
            if (rule is null)
            {
                item.Decision = DeployDecisionValue.Apply;
                continue;
            }

            item.SkipRule = rule.Pattern;
            item.Decision = DeployDecisionValue.Skip;
            count++;
        }
        return count;
    }

    private sealed class Rule
    {
        public string Pattern { get; }
        private readonly Regex[] _segments;
        private readonly bool _isFolder;

        public Rule(string pattern)
        {
            Pattern = pattern;
            _isFolder = pattern.EndsWith('/') || pattern.EndsWith('\\');
            _segments = pattern
                .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
                .Select(ToRegex)
                .ToArray();
        }

        public bool IsMatch(string[] parts)
        {
            if (_segments.Length == 0) return false;

            if (_isFolder)
            {
                // 파일명을 제외한 폴더 경로 중 연속된 구간이 패턴과 일치하는지 확인
                for (var start = 0; start + _segments.Length <= parts.Length - 1; start++)
                    if (MatchesAt(parts, start)) return true;
                return false;
            }

            // 상대 경로의 끝부분과 일치하는지 확인
            var offset = parts.Length - _segments.Length;
            return offset >= 0 && MatchesAt(parts, offset);
        }

        private bool MatchesAt(string[] parts, int start)
        {
            for (var i = 0; i < _segments.Length; i++)
                if (!_segments[i].IsMatch(parts[start + i])) return false;
            return true;
        }

        private static Regex ToRegex(string glob) =>
            new("^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
