<#
.SYNOPSIS
    Every option the user can act on is named in the user's guide (#358).

.DESCRIPTION
    docs\how-to-use.md is both the repository's guide and the application's F1
    help - one file, linked into the package as Help\how-to-use.md - so a
    control that is not in it is undocumented in both places at once.

    WHY THIS EXISTS. An audit on 30 Aug 2026 read the guide against the code and
    found the drift a release makes expensive: an elevation-mask slider and a
    self-test sweep that credits every subsystem, both shipped that week and
    neither mentioned; a description of unavailable controls that "do nothing",
    written before they were disabled with a reason; and - the one that matters -
    a Holdover section describing ONE editable threshold when the page has two
    settings, of which the one it described cannot be set at all. Every one of
    those was added by someone who changed a page and did not think to open the
    guide. Nothing was checking.

    WHAT IT CHECKS. Each interactive control in Views\*.xaml that carries a
    literal label must have that label somewhere in the guide. Labels only:
    a user looks a control up by what it says on it, not by its x:Name, and a
    name-based check produces noise (ApplyDurationLimitButton against "Apply
    duration limit") that teaches people to ignore it.

    WHAT IT CANNOT CHECK. That the guide is CORRECT. The Holdover error above
    would have passed this gate, because the words were all present and merely
    describing the wrong quantity. This makes it impossible to ship an option
    nobody wrote about; it cannot make it impossible to write something wrong.
    That is what docs\manual-qa.md and a reader are for.

.PARAMETER Guide
    The document to check against. Defaults to docs\how-to-use.md.

.NOTES
    THE ALLOWLIST IS A REDIRECTION, NOT AN EXEMPTION. A guide should read as
    prose, so it says "the cable length in metres" where the control's header
    reads "Cable length (metres)". Such a control gets a row below giving the
    guide's own phrasing - and THAT PHRASING IS ITSELF REQUIRED TO BE IN THE
    GUIDE. So a row cannot quietly turn into a hole: delete the sentence it
    points at and the gate fails on the row rather than passing on the silence.
#>

[CmdletBinding()]
param(
    [string]$Guide
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Guide) { $Guide = Join-Path $repo 'docs\how-to-use.md' }

# Controls a user acts on. A TextBlock is not an option; a Border is not an option.
$interactive = @(
    'Button', 'HyperlinkButton', 'ToggleButton', 'ToggleSwitch', 'CheckBox',
    'RadioButton', 'ComboBox', 'NumberBox', 'TextBox', 'Slider', 'AutoSuggestBox',
    'ToggleSplitButton', 'SplitButton', 'MenuFlyoutItem', 'ToggleMenuFlyoutItem',
    'DropDownButton'
)

# Control label -> the wording the guide uses for it. Both sides are checked.
$phrasedDifferently = @(
    @{ Label = 'Cable length (metres)';             Guide = 'the cable length in metres' }
    @{ Label = 'Delay (nanoseconds)';               Guide = 'enter the delay directly in nanoseconds' }
    @{ Label = 'Holdover duration limit (seconds)'; Guide = 'Holdover duration limit' }
    @{ Label = 'Filter entries';                    Guide = 'the entries the receiver itself keeps, filterable' }
)

function ConvertTo-Comparable([string]$text) {
    if (-not $text) { return '' }

    $text = $text -replace [char]0x2019, "'"
    $text = $text -replace '[*_`]', ''       # markdown emphasis and code spans
    $text = $text -replace '\s+', ' '        # the guide wraps its lines; a label does not
    return $text.Trim().ToLowerInvariant()
}

$guideText = ConvertTo-Comparable (Get-Content $Guide -Raw)

$views = Get-ChildItem (Join-Path $repo 'src\WinZ3805A\Views') -Filter '*.xaml' -ErrorAction Stop
$checked = 0
$undocumented = @()

foreach ($view in $views) {
    try {
        [xml]$xaml = Get-Content $view.FullName -Raw
    }
    catch {
        Write-Error "Could not parse $($view.Name): $_"
        exit 1
    }

    foreach ($node in $xaml.SelectNodes('//*')) {
        if ($interactive -notcontains $node.LocalName) { continue }

        # A bound label - Content="{x:Bind ...}" - is supplied at run time and
        # cannot be read here. Those are checked by a person, not by this.
        $label = $null
        foreach ($attribute in @('Content', 'Header', 'PlaceholderText', 'Text')) {
            $value = $node.GetAttribute($attribute)
            if ($value -and -not $value.StartsWith('{')) {
                $label = $value
                break
            }
        }

        if (-not $label) { continue }

        $comparable = (ConvertTo-Comparable $label).TrimEnd([char]0x2026, '.', ' ')
        if ($comparable.Length -lt 3) { continue }

        $checked++
        if ($guideText.Contains($comparable)) { continue }

        $row = $phrasedDifferently | Where-Object { (ConvertTo-Comparable $_.Label) -eq $comparable }

        if (-not $row) {
            $undocumented += [pscustomobject]@{
                View  = $view.Name
                Name  = $node.GetAttribute('x:Name')
                Label = $label
                Why   = 'not in the guide, and no row says the guide words it differently'
            }
            continue
        }

        # The row's own claim, checked. An allowlist entry pointing at a sentence
        # that has since been deleted is a hole with a comment over it.
        if (-not $guideText.Contains((ConvertTo-Comparable $row.Guide))) {
            $undocumented += [pscustomobject]@{
                View  = $view.Name
                Name  = $node.GetAttribute('x:Name')
                Label = $label
                Why   = "the allowlist says the guide words this as `"$($row.Guide)`" - and it does not"
            }
        }
    }
}

Write-Host "Checked $checked labelled control(s) in $($views.Count) page(s) against $([IO.Path]::GetFileName($Guide))."

if ($undocumented.Count -gt 0) {
    Write-Host ''
    Write-Host 'FAIL: options the guide does not cover.' -ForegroundColor Red
    Write-Host ''

    foreach ($item in $undocumented) {
        Write-Host ("  {0}  {1}" -f $item.View, $item.Name)
        Write-Host ("      label: {0}" -f $item.Label)
        Write-Host ("      {0}" -f $item.Why)
        Write-Host ''
    }

    Write-Host 'The guide is also the F1 help, so an option missing there is missing in both.'
    Write-Host 'Add it to docs\how-to-use.md, or - if the guide words it differently - add a row'
    Write-Host 'to $phrasedDifferently in this script giving the guide''s own phrasing.'
    exit 1
}

Write-Host 'PASS: every labelled option is named in the guide.' -ForegroundColor Green
exit 0
