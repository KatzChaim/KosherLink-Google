# מפרסם גרסה חדשה של KosherLink ב־GitHub. כל ההתקנות מתעדכנות לבד תוך יום.
#
#   .\publish.ps1                          מעלה את המספר האחרון בגרסה ב־1 (למשל 1.0.3 ל־1.0.4)
#   .\publish.ps1 -Version 1.1.0           קובע מספר גרסה מסוים
#   .\publish.ps1 -Notes "תיקון בגיבוי"    הסבר שיוצג בדף הגרסה
#   .\publish.ps1 -DryRun                  רק בודק ומראה מה ייעשה, בלי לשנות כלום
#
# השלבים: בדיקות, עדכון מספר הגרסה, בנייה, הכנת zip, commit ו־push, ופרסום Release.
param(
    [string]$Version,
    [string]$Notes = "",
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$repo = 'KatzChaim/KosherLink'
$programCs = 'src\Program.cs'
$exe = 'dist\KosherLink.exe'

function Step($t) { Write-Host "`n== $t" -ForegroundColor Cyan }
function Fail($t) { Write-Host "שגיאה: $t" -ForegroundColor Red; exit 1 }
# Windows PowerShell 5.1 מתייחס לכל פלט ב־stderr של תוכנה חיצונית כשגיאה, ולכן בודקים רק את קוד היציאה
function Invoke-Quiet { $ErrorActionPreference = 'Continue'; $exe, $rest = $args; & $exe @rest 2>&1 | Out-Null; $LASTEXITCODE }
function Invoke-Git {
    $ErrorActionPreference = 'Continue'
    & git.exe @args 2>&1 | ForEach-Object { Write-Host "   $_" }
    if ($LASTEXITCODE -ne 0) { Fail "git $($args -join ' ') נכשל" }
}

# ---- 1. בדיקות
Step "בדיקות"
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail "git לא מותקן" }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { Fail "GitHub CLI (gh) לא מותקן" }
if ((Invoke-Quiet gh.exe auth status) -ne 0) { Fail "לא מחובר ל־GitHub. הרץ: gh auth login" }
# המפתח הפרטי לחתימת עדכונים. הוא נמצא רק במחשב הזה, לא ב־GitHub, וחובה לשמור ממנו גיבוי!
$keyFile = Join-Path $env:USERPROFILE '.kosherlink\update-signing-key.xml'
if (-not (Test-Path $keyFile)) { Fail "לא נמצא המפתח לחתימת עדכונים: $keyFile (שחזר אותו מהגיבוי)" }
$k = New-Object Security.Cryptography.RSACryptoServiceProvider
$k.FromXmlString([IO.File]::ReadAllText($keyFile))
$publicXml = $k.ToXmlString($false)
if (-not ([IO.File]::ReadAllText((Resolve-Path 'src\WhatsApp.cs'))).Contains($publicXml)) { Fail "המפתח הפרטי לא תואם למפתח הציבורי שבקוד (src\WhatsApp.cs). ההתקנות לא יקבלו עדכון חתום בו" }
$branch = (& git.exe rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne 'main') { Fail "צריך להיות על הענף main (כרגע: $branch)" }
if ((Invoke-Quiet git.exe fetch -q origin main) -ne 0) { Fail "אין גישה ל־GitHub (git fetch נכשל)" }
$behind = (& git.exe rev-list --count HEAD..origin/main).Trim()
if ($behind -ne '0') { Fail "ב־GitHub יש $behind שינויים שאין כאן. הרץ קודם: git pull" }

$src = [IO.File]::ReadAllText((Resolve-Path $programCs), [Text.Encoding]::UTF8)
$m = [regex]::Match($src, 'public const string Version = "(\d+)\.(\d+)\.(\d+)";')
if (-not $m.Success) { Fail "לא נמצא מספר הגרסה ב־$programCs" }
$current = [version]"$($m.Groups[1]).$($m.Groups[2]).$($m.Groups[3])"
if (-not $Version) { $Version = "$($current.Major).$($current.Minor).$($current.Build + 1)" }
$new = $null
if (-not [version]::TryParse($Version, [ref]$new) -or $new.Revision -ne -1 -or $new.Build -lt 0) { Fail "מספר גרסה לא תקין: $Version (צריך להיות בצורה 1.2.3)" }
if ($new -le $current) { Fail "הגרסה החדשה ($Version) צריכה להיות גבוהה מהנוכחית ($current)" }
if ((Invoke-Quiet gh.exe release view "v$Version" --repo $repo) -eq 0) { Fail "הגרסה v$Version כבר קיימת ב־GitHub" }

$changes = & git.exe status --porcelain
Write-Host "גרסה נוכחית: $current  ←  גרסה חדשה: $Version"
if ($changes) { Write-Host "שינויים שייכנסו לגרסה:"; $changes | ForEach-Object { Write-Host "   $_" } }
else { Write-Host "אין שינויים בקבצים (מתפרסמת אותה גרסה של הקוד עם מספר חדש)" }

if ($DryRun) { Write-Host "`nבדיקה בלבד (DryRun): הכל תקין, לא בוצע שום שינוי." -ForegroundColor Green; exit 0 }

# ---- 2. מספר גרסה
Step "מעדכן את מספר הגרסה ל־$Version"
$src = $src.Replace($m.Value, "public const string Version = `"$Version`";")
[IO.File]::WriteAllText((Resolve-Path $programCs), $src, (New-Object Text.UTF8Encoding $false))

# ---- 3. בנייה
Step "בונה"
if (Test-Path $exe) {
    # אם התוכנה פועלת מתוך dist, אי אפשר לדרוס את הקובץ, אבל אפשר לשנות לו שם
    try { [IO.File]::Open((Resolve-Path $exe), 'Open', 'ReadWrite', 'None').Close() }
    catch { Rename-Item $exe ("KosherLink.running-" + (Get-Date -Format 'yyyyMMddHHmmss') + ".exe") }
}
& powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
if ($LASTEXITCODE -ne 0) { Fail "הבנייה נכשלה" }
$built = (Get-Item $exe).VersionInfo.FileVersion
if ($built -notlike "$Version*") { Fail "הקובץ שנבנה הוא בגרסה $built ולא $Version" }
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash

# ---- 3א. חתימה במפתח הפרטי: ההתקנות מסרבות לקבל עדכון בלי חתימה תקינה
Step "חותם"
$sigFile = "$exe.sig"
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
$rsa.FromXmlString([IO.File]::ReadAllText($keyFile))
$sig = $rsa.SignData([IO.File]::ReadAllBytes((Resolve-Path $exe)), [Security.Cryptography.SHA256]::Create())
[IO.File]::WriteAllText((Join-Path (Resolve-Path 'dist') 'KosherLink.exe.sig'), [Convert]::ToBase64String($sig))
$check = New-Object Security.Cryptography.RSACryptoServiceProvider
$check.FromXmlString($publicXml)
if (-not $check.VerifyData([IO.File]::ReadAllBytes((Resolve-Path $exe)), [Security.Cryptography.SHA256]::Create(), $sig)) { Fail "בדיקת החתימה נכשלה" }
Write-Host "   נחתם ואומת"

# ---- 4. חבילת zip (התוכנה + הוראות)
Step "מכין חבילה"
$tmp = Join-Path $env:TEMP "kosherlink-pack"
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory $tmp | Out-Null
Copy-Item $exe $tmp
Copy-Item 'release\הוראות.txt' $tmp
$zip = 'release\KosherLink.zip'
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$tmp\*" -DestinationPath $zip
Remove-Item $tmp -Recurse -Force

# מתקין תעודת הסינון: עותק של התוכנה בשם KosherLink-Filter.exe פותח רק את חלון הבחירה של תעודת הסינון
$filterDir = 'dist\filters'
if (Test-Path $filterDir) { Remove-Item $filterDir -Recurse -Force }
New-Item -ItemType Directory $filterDir | Out-Null
$filterFile = "$filterDir\KosherLink-Filter.exe"
Copy-Item $exe $filterFile

# ---- 5. GitHub: commit, תגית, push
# מכאן והלאה רק תוכנות חיצוניות, שכותבות התקדמות ל־stderr. הכישלונות נבדקים לפי קוד היציאה
$ErrorActionPreference = 'Continue'
Step "מעלה ל־GitHub"
Invoke-Git add -A
$msg = "KosherLink $Version"
if ($Notes) { $msg += "`n`n$Notes" }
Invoke-Git commit -q -m $msg
Invoke-Git tag "v$Version"
Invoke-Git push -q origin main "v$Version"

# ---- 6. Release (ממנו כל ההתקנות מתעדכנות)
Step "מפרסם Release"
$body = "## KosherLink $Version`n`n"
if ($Notes) { $body += "$Notes`n`n" }
$body += "**להורדה:** ``KosherLink.exe``, או ``KosherLink.zip`` שכולל גם קובץ הוראות. מי שהתוכנה כבר מותקנת אצלו יקבל את העדכון לבד, תוך יום.`n`n"
$body += "**תעודת סינון אינטרנט:** מי שגולש דרך סינון (נטפרי, נטספארק, רימון, אתרוג ועוד) מוריד ומפעיל את ``KosherLink-Filter.exe``, בוחר את הסינון שלו ולוחץ ""התקן"". אותו חלון נמצא גם בתוכנה עצמה. פרטים ב־[README](https://github.com/$repo#סינון-אינטרנט).`n`n"
$body += "SHA-256 של ``KosherLink.exe``: ``$hash``"
$notesFile = Join-Path $env:TEMP "kosherlink-notes.md"
[IO.File]::WriteAllText($notesFile, $body, (New-Object Text.UTF8Encoding $false))
& gh.exe release create "v$Version" $exe $sigFile $zip $filterFile --repo $repo --title "KosherLink $Version" --notes-file $notesFile --verify-tag
if ($LASTEXITCODE -ne 0) { Fail "יצירת ה־Release נכשלה (הקוד כבר הועלה, אפשר להריץ שוב רק את gh release create)" }
Remove-Item $notesFile

# ---- 7. אימות: מה שההתקנות יראו
# (בלי --jq: ב־PowerShell 5.1 גרשיים בתוך ארגומנט לא מגיעים לתוכנה החיצונית)
$latest = (& gh.exe api "repos/$repo/releases/latest") -join "`n" | ConvertFrom-Json
$digest = ($latest.assets | Where-Object { $_.name -eq 'KosherLink.exe' }).digest
if ($latest.tag_name -ne "v$Version") { Fail "ב־GitHub הגרסה האחרונה היא $($latest.tag_name) ולא v$Version" }
if ($digest -ne "sha256:$($hash.ToLower())") { Fail "ב־GitHub הקובץ האחרון לא תואם ($digest)" }
if (-not ($latest.assets | Where-Object { $_.name -eq 'KosherLink.exe.sig' })) { Fail "קובץ החתימה לא הועלה ל־GitHub" }
Write-Host "`nגרסה $Version פורסמה: https://github.com/$repo/releases/tag/v$Version" -ForegroundColor Green
Write-Host "כל ההתקנות יתעדכנו לבד תוך יום. כדי לעדכן מיד: קליק ימני על הסמל ← בדוק עדכונים עכשיו."
