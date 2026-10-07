<#
.SYNOPSIS
Publishes docs\wiki\*.md to the GitHub Wiki (https://github.com/throndir2/AudioTranscriber/wiki).

.DESCRIPTION
Clones the wiki repository, replaces its pages with docs\wiki, commits and pushes. GitHub creates the wiki
repository only after its first page is saved in the browser, so create any first page once before running this.
#>
[CmdletBinding()]
param([string]$Message = 'Update wiki from docs/wiki')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$work = Join-Path ([IO.Path]::GetTempPath()) ('AudioTranscriber.Wiki.' + [guid]::NewGuid().ToString('N'))
git clone -q https://github.com/throndir2/AudioTranscriber.wiki.git $work
if ($LASTEXITCODE -ne 0) { throw 'Could not clone the wiki. Create its first page at https://github.com/throndir2/AudioTranscriber/wiki, then rerun.' }
try {
    Get-ChildItem $work -Filter *.md | Remove-Item
    Copy-Item (Join-Path $root 'docs\wiki\*.md') $work -Exclude README.md
    git -C $work add -A
    if (git -C $work status --porcelain) {
        git -C $work commit -q -m $Message
        git -C $work push -q
        if ($LASTEXITCODE -ne 0) { throw 'Push to the wiki failed.' }
        'Wiki published.'
    } else { 'Wiki already up to date.' }
} finally { Remove-Item -Recurse -Force $work }
