# Regenerates test/fixtures/native-fills-describe-api.json — the static "describe_api-equivalent"
# dump used by test/api-contract-ledger.test.mjs to pin worker/api-contract.json ledger entries.
#
# Source of truth: worker/generated/VGCore.Interop.dll (build-time TypeLibConverter output pinned by
# worker/generated/fingerprint.json). Reflection only: no CorelDRAW instance, no COM calls, no mutation.
# Live-observed behavior (defaults such as steps=256, EdgePad renormalization, drop-shadow semantics)
# is NOT reflection data and lives in worker/api-contract.json native_fills (live gates T4-T6).
#
# Usage: pwsh scripts/dump-native-fills-fixture.ps1 [-InteropPath <path>] [-OutPath <path>]
[CmdletBinding()]
param(
    [string]$InteropPath = (Join-Path $PSScriptRoot '..\worker\generated\VGCore.Interop.dll'),
    [string]$OutPath = (Join-Path $PSScriptRoot '..\test\fixtures\native-fills-describe-api.json'),
    [string]$FingerprintPath = (Join-Path $PSScriptRoot '..\worker\generated\fingerprint.json')
)

$ErrorActionPreference = 'Stop'
$interop = [System.IO.Path]::GetFullPath($InteropPath)
$fingerprint = [System.IO.Path]::GetFullPath($FingerprintPath)
$out = [System.IO.Path]::GetFullPath($OutPath)
if (-not (Test-Path -LiteralPath $interop)) { throw "Interop assembly not found: $interop" }

# Members contractually relevant to corel_style_shapes (fountain fills), corel_set_transparency and
# corel_apply_drop_shadow. get_/set_ accessors are recorded separately, mirroring the ledger style.
$spec = [ordered]@{
    'IVGEffect' = @('Clear')
    'IVGFill' = @('ApplyFountainFill')
    'IVGFountainColor' = @('get_Color', 'get_Position')
    'IVGFountainColors' = @('Add', 'get_Count', 'get_First', 'get_Item', 'get_Last')
    'IVGFountainFill' = @(
        'MakeOpaque', 'SetAngle', 'SetEdgePad', 'SetTransformations',
        'get_Angle', 'get_BlendType', 'set_BlendType',
        'get_CenterOffsetX', 'set_CenterOffsetX', 'get_CenterOffsetY', 'set_CenterOffsetY',
        'get_Colors', 'get_EdgePad', 'get_EndColor', 'set_EndColor',
        'get_EndX', 'set_EndX', 'get_EndY', 'set_EndY',
        'get_MergeMode', 'set_MergeMode', 'get_MidPoint', 'set_MidPoint',
        'get_ScaleX', 'set_ScaleX', 'get_ScaleY', 'set_ScaleY', 'get_Skew', 'set_Skew',
        'get_SmoothBlend', 'set_SmoothBlend', 'get_SpreadMethod', 'set_SpreadMethod',
        'get_StartColor', 'set_StartColor', 'get_StartX', 'set_StartX', 'get_StartY', 'set_StartY',
        'get_Steps', 'set_Steps', 'get_Type', 'set_Type'
    )
    'IVGShape' = @('ClearEffect', 'CreateDropShadow')
    'IVGTransparency' = @(
        'ApplyFountainTransparency', 'ApplyNoTransparency', 'ApplyUniformTransparency',
        'Freeze', 'Unfreeze', 'get_AppliedTo', 'set_AppliedTo', 'get_Frozen',
        'get_MergeMode', 'set_MergeMode'
    )
}

# Enums recorded with typelib member names. cdrMergeMode is the schema whitelist (the full enum
# exists in the type library; only whitelisted merge modes are accepted by the tools).
$enumWhitelist = [ordered]@{
    'cdrFountainFillType' = $null
    'cdrFountainFillSpreadMethod' = $null
    'cdrFountainFillBlendType' = $null
    'cdrTransparencyAppliedTo' = $null
    'cdrDropShadowType' = $null
    'cdrFeatherType' = $null
    'cdrEdgeType' = $null
    'cdrMergeMode' = @('cdrMergeNormal', 'cdrMergeMultiply', 'cdrMergeScreen', 'cdrMergeOverlay', 'cdrMergeBehind')
}

# Ledger clr_signature style: short CLR primitives (Void/Double/Int32/Boolean), full VGCore.* and
# System.String names — mirrors the existing entries (e.g. "Void ApplyUniformFill(VGCore.Color)").
function Short-TypeName([string]$fullName) {
    switch ($fullName) {
        'System.Void' { 'Void' }
        'System.Double' { 'Double' }
        'System.Int32' { 'Int32' }
        'System.Boolean' { 'Boolean' }
        default { $fullName }
    }
}

# sdk_url convention mirrored from the existing ledger entries (Context7 /websites/community_coreldraw_sdk_api_draw):
# property accessors -> /p/<interface-sans-IVG>.<propertyName>, methods -> /m/<interface-sans-IVG>.<member>.
function Sdk-Url([string]$interface, [string]$member) {
    $short = $interface.Substring(3).ToLowerInvariant()
    if ($member -like 'get_*' -or $member -like 'set_*') {
        $prop = $member.Substring(4).ToLowerInvariant()
        "https://community.coreldraw.com/sdk/api/draw/27/p/$short.$prop`?lang=cs"
    } else {
        "https://community.coreldraw.com/sdk/api/draw/27/m/$short.$($member.ToLowerInvariant())`?lang=cs"
    }
}

$assembly = [System.Reflection.Assembly]::LoadFile($interop)
$signatures = New-Object System.Collections.Generic.List[object]
foreach ($interface in $spec.Keys) {
    $type = $assembly.GetType("VGCore.$interface")
    if ($null -eq $type) { throw "Interface VGCore.$interface not found in the interop assembly." }
    $all = $type.GetMethods() | Where-Object { $_.DeclaringType.Name -eq $interface }
    foreach ($member in ($spec[$interface] | Sort-Object -Property { $_ } -CaseSensitive)) {
        $method = $all | Where-Object { $_.Name -eq $member }
        if ($null -eq $method) { throw "Member $interface.$member not found in the interop assembly." }
        $returnShort = Short-TypeName $method.ReturnType.FullName
        $params = @()
        foreach ($p in $method.GetParameters()) {
            $default = $null
            if ($p.IsOptional -and $null -ne $p.DefaultValue -and $p.DefaultValue -isnot [System.Reflection.Missing]) {
                if ($p.DefaultValue -is [enum]) { $default = $p.DefaultValue.ToString() }
                else { $default = $p.DefaultValue }
            }
            $params += [pscustomobject][ordered]@{
                name = $p.Name
                type = $p.ParameterType.FullName
                optional = $p.IsOptional
                default = if ($p.IsOptional) { $default } else { 'missing' }
            }
        }
        $signatureParams = ($method.GetParameters() | ForEach-Object { Short-TypeName $_.ParameterType.FullName }) -join ', '
        $signatures.Add([pscustomobject][ordered]@{
            interface = $interface
            member = $member
            clr_signature = "$returnShort $member($signatureParams)"
            sdk_url = (Sdk-Url $interface $member)
            parameters = $params
        })
    }
}

$enumValues = [ordered]@{}
foreach ($enumName in $enumWhitelist.Keys) {
    $enumType = $assembly.GetType("VGCore.$enumName", $false)
    if ($null -eq $enumType) { throw "Enum VGCore.$enumName not found in the interop assembly." }
    $allowed = $enumWhitelist[$enumName]
    foreach ($name in [enum]::GetNames($enumType)) {
        if ($null -ne $allowed -and $allowed -notcontains $name) { continue }
        $enumValues["$enumName.$name"] = [int][enum]::Parse($enumType, $name)
    }
}

$fp = Get-Content -LiteralPath $fingerprint -Raw | ConvertFrom-Json
$fixture = [pscustomobject][ordered]@{
    provenance = [pscustomobject][ordered]@{
        generator = 'scripts/dump-native-fills-fixture.ps1'
        method = 'static .NET reflection over the build-time generated interop assembly worker/generated/VGCore.Interop.dll; no COM calls and no CorelDRAW instance'
        typelib = [pscustomobject][ordered]@{
            guid = $fp.typelib_guid
            major = $fp.major
            minor = $fp.minor
            syskind = $fp.syskind
            source_sha256 = $fp.source_sha256
        }
        assembly_sha256 = $fp.assembly_sha256
        parameter_metadata = 'optional flags and default values come from the COM parameter metadata preserved by the TypeLibConverter-generated assembly; object-typed parameters without a COM default serialize as null'
        sdk_urls = 'built with the ledger convention https://community.coreldraw.com/sdk/api/draw/27/{m|p}/<interface-sans-IVG>.<member>?lang=cs (Context7 /websites/community_coreldraw_sdk_api_draw mirror)'
        scope = 'members used or contractually relevant for corel_style_shapes fountain fills, corel_set_transparency and corel_apply_drop_shadow; cdrMergeMode is recorded as the schema whitelist, the full enum exists in the type library'
        observed_behavior_note = 'live-observed defaults and normalizations (steps=256, EdgePad renormalization, drop-shadow semantics) are not reflection data; they are recorded in worker/api-contract.json native_fills from live gates T4-T6'
    }
    signatures = $signatures
    enum_values = $enumValues
}

$json = $fixture | ConvertTo-Json -Depth 12
$directory = Split-Path -Parent $out
if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
Set-Content -LiteralPath $out -Value $json -Encoding utf8NoBOM
Write-Output "Wrote $out with $($signatures.Count) signature entries and $($enumValues.Count) enum values."
