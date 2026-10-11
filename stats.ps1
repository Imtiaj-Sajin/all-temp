# Shows how AllTemp is doing on GitHub: downloads per release file (all-time, includes winget installs),
# visitors and clones (GitHub keeps only the last 14 days), where visitors came from, and stars.
# Needs the GitHub CLI signed in as the repo owner: gh auth login
param([string]$Repo = "Imtiaj-Sajin/all-temp")

$ErrorActionPreference = "Stop"
# Parentheses make PowerShell 5 enumerate JSON arrays instead of passing them as one object.
function Api($path) { (gh api $path | ConvertFrom-Json) | ForEach-Object { $_ } }

$r = Api "repos/$Repo"
"`n$Repo   stars: $($r.stargazers_count)   forks: $($r.forks_count)   watching: $($r.subscribers_count)   open issues: $($r.open_issues_count)"

"`nDOWNLOADS (all time)"
$total = 0
foreach ($rel in @(Api "repos/$Repo/releases")) {
    foreach ($a in $rel.assets) {
        "  {0,-10} {1,-24} {2,7}" -f $rel.tag_name, $a.name, $a.download_count
        $total += $a.download_count
    }
}
"  {0,-35} {1,7}" -f "TOTAL", $total

try {
    $views = Api "repos/$Repo/traffic/views"
    $clones = Api "repos/$Repo/traffic/clones"
    "`nLAST 14 DAYS   repo page views: $($views.count) ($($views.uniques) unique visitors)   clones: $($clones.count) ($($clones.uniques) unique)"
    foreach ($d in $views.views | Where-Object { $_.count -gt 0 }) { "  {0}  {1,4} views  {2,4} visitors" -f $d.timestamp.Substring(0, 10), $d.count, $d.uniques }

    $refs = @(Api "repos/$Repo/traffic/popular/referrers")
    if ($refs) {
        "`nWHERE VISITORS CAME FROM (14 days)"
        foreach ($x in $refs) { "  {0,-30} {1,5} views  {2,4} visitors" -f $x.referrer, $x.count, $x.uniques }
    }
    $paths = @(Api "repos/$Repo/traffic/popular/paths")
    if ($paths) {
        "`nMOST VIEWED PAGES (14 days)"
        foreach ($x in $paths | Select-Object -First 5) { "  {0,-50} {1,5}" -f $x.path, $x.count }
    }
}
catch { "`n(Traffic numbers need push access to the repo: $_)" }
""
