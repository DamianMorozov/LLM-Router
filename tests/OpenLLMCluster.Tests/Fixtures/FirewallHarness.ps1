# Проверяет реальную привязку параметров скрипта без обращения к брандмауэру ОС
$ErrorActionPreference = 'Stop'
$global:rule = $null
$global:created = 0
$global:updated = 0

function Get-NetFirewallRule {
    [CmdletBinding()] param([string]$Name)
    $global:rule
}
function New-NetFirewallRule {
    [CmdletBinding()] param($Name, $DisplayName, $Direction, $Action, $Enabled, $Protocol, $LocalPort, $Profile, $RemoteAddress)
    $global:created++
    $global:rule = [pscustomobject]@{
        Name = $Name; Direction = $Direction; Action = $Action; Enabled = $Enabled
        Protocol = $Protocol; LocalPort = $LocalPort; Profile = $Profile; RemoteAddress = $RemoteAddress
    }
    $global:rule
}
function Set-NetFirewallRule {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$InputObject, $Direction, $Action, $Enabled, $Profile)
    process {
        $global:updated++
        $InputObject.Direction = $Direction; $InputObject.Action = $Action
        $InputObject.Enabled = $Enabled; $InputObject.Profile = $Profile
    }
}
function Get-NetFirewallPortFilter {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$InputObject)
    process { $InputObject }
}
function Get-NetFirewallAddressFilter {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$InputObject)
    process { $InputObject }
}
function Set-NetFirewallPortFilter {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$InputObject, $Protocol, $LocalPort)
    process { $InputObject.Protocol = $Protocol; $InputObject.LocalPort = $LocalPort }
}
function Set-NetFirewallAddressFilter {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$InputObject, $RemoteAddress)
    process { $InputObject.RemoteAddress = $RemoteAddress }
}

__APPLY_RULE__
if ($created -ne 1 -or $updated -ne 0 -or $rule.Name -ne 'Open-LLM-Cluster-Agent-TCP-5100') { throw 'Rule was not created' }
$rule.Direction = 'Outbound'; $rule.Profile = 'Public'; $rule.RemoteAddress = 'Any'
$rule.LocalPort = 9999; $rule.Protocol = 'UDP'; $rule.Enabled = 'False'; $rule.Action = 'Block'
__APPLY_RULE__
if ($created -ne 1 -or $updated -ne 1) { throw 'Rule update created a duplicate' }
if ($rule.Direction -ne 'Inbound' -or $rule.Action -ne 'Allow' -or $rule.Enabled -ne 'True') { throw 'Incorrect rule action' }
if ($rule.Profile -ne 'Private' -or $rule.RemoteAddress -ne 'LocalSubnet') { throw 'Incorrect network scope' }
if ($rule.LocalPort -ne 5100 -or $rule.Protocol -ne 'TCP') { throw 'Incorrect protocol or port' }
Write-Output 'RULE_POLICY_OK'
