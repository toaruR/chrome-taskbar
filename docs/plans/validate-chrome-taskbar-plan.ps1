[CmdletBinding()] param([Parameter(Mandatory)][string]$PlanPath,[Parameter(Mandatory)][string]$DesignPath,[string]$EvidencePath,[switch]$RequirePocEvidence,[switch]$RequirePocGatePass,[switch]$RequireAcceptanceEvidence)
$ErrorActionPreference='Stop';
 $integer=0;
 $n=$null;
 $rounds=$null;
 $raw=Get-Content -Raw $PlanPath;
 $p=$raw|ConvertFrom-Json;
 $d=Get-Content -Raw $DesignPath
$json=[Text.Json.JsonDocument]::Parse($raw).RootElement
function Assert-Props($o,[string[]]$required,[string[]]$allowed,[string]$where){if($o.ValueKind-ne'Object'){throw "${where}: expected object."};
$names=@($o.EnumerateObject()|ForEach-Object Name);
if(@($required|Where-Object{$_-notin$names}).Count-or@($names|Where-Object{$_-notin$allowed}).Count){throw "${where}: invalid properties."}}
function Assert-String($v,[int]$min,[int]$max,[string]$where){if($v.ValueKind-ne'String'-or$v.GetString().Length-lt$min-or$v.GetString().Length-gt$max){throw "${where}: invalid string."}}
function Assert-Array($v,[int]$min,[int]$max,[string]$where){if($v.ValueKind-ne'Array'-or$v.GetArrayLength()-lt$min-or$v.GetArrayLength()-gt$max){throw "${where}: invalid array."}}
Assert-Props $json @('plan_version','summary','assumptions','tasks') @('plan_version','summary','assumptions','tasks') '$'
if($json.GetProperty('plan_version').ValueKind-ne'Number'-or!$json.GetProperty('plan_version').TryGetInt32([ref]$integer)-or$json.GetProperty('plan_version').GetInt32()-lt 1){throw 'Invalid plan version.'};
Assert-String $json.GetProperty('summary') 40 4000 '$.summary';
Assert-Array $json.GetProperty('assumptions') 0 2147483647 '$.assumptions';
foreach($a in $json.GetProperty('assumptions').EnumerateArray()){Assert-String $a 0 1000 '$.assumptions[]'};
Assert-Array $json.GetProperty('tasks') 1 200 '$.tasks'
$ids=@();
$previous=$null;
foreach($t in $json.GetProperty('tasks').EnumerateArray()){$where="`$.tasks[$ids.Count]";
Assert-Props $t @('id','title','intent','design_refs','depends_on','changes','acceptance','verify','estimate_rounds') @('id','title','intent','design_refs','depends_on','changes','acceptance','verify','estimate_rounds') $where;
Assert-String $t.GetProperty('id') 1 200 "$where.id";
$id=$t.GetProperty('id').GetString();
if($id-notmatch'^T[0-9]{3}$'-or$id-in$ids){throw "$where.id: invalid or duplicate."};
$ids+=$id;
if($t.GetProperty('estimate_rounds').ValueKind-ne'Number'-or!$t.GetProperty('estimate_rounds').TryGetInt32([ref]$integer)-or$integer-lt1-or$integer-gt2){throw "${id}: estimate_rounds must be 1 or 2."};
Assert-String $t.GetProperty('title') 1 200 "$where.title";
Assert-String $t.GetProperty('intent') 20 2000 "$where.intent";
foreach($field in 'design_refs','depends_on','changes','acceptance','verify'){Assert-Array $t.GetProperty($field) $(if($field-eq'depends_on'){0}else{1}) 2147483647 "$where.$field"};
$deps=@();
foreach($dep in $t.GetProperty('depends_on').EnumerateArray()){Assert-String $dep 1 200 "$where.depends_on[]";
$value=$dep.GetString();
if($value-notmatch'^T[0-9]{3}$'-or$value-in$deps){throw "$where.depends_on: invalid."};
$deps+=$value};
if($previous){if($deps.Count-ne1-or$deps[0]-ne$previous){throw "${id}: invalid serial dependency."}}elseif($deps.Count){throw 'Root has dependency.'};
$previous=$id;
foreach($r in $t.GetProperty('design_refs').EnumerateArray()){Assert-String $r 1 200 "$where.design_refs[]";
if(!$d.Contains($r.GetString())){throw "${id}: unknown ref."}};
foreach($c in $t.GetProperty('changes').EnumerateArray()){Assert-Props $c @('path','kind') @('path','kind','note') "$where.changes[]";
Assert-String $c.GetProperty('path') 1 1024 "$where.changes[].path";
if($c.GetProperty('kind').ValueKind-ne'String'-or$c.GetProperty('kind').GetString()-notin'add','modify','delete'){throw "${id}: invalid change kind."}};
foreach($a in $t.GetProperty('acceptance').EnumerateArray()){Assert-String $a 10 1000 "$where.acceptance[]"};$acceptance=@($t.GetProperty('acceptance').EnumerateArray()|ForEach-Object GetString)-join"`n";if($acceptance-notmatch"Stopping after $id leaves"){throw "${id}: missing safe residual boundary."};
foreach($v in $t.GetProperty('verify').EnumerateArray()){Assert-Props $v @('command','expect_exit_code') @('command','expect_exit_code','note') "$where.verify[]";
Assert-String $v.GetProperty('command') 3 1000 "$where.verify[].command";
if($v.GetProperty('command').GetString()-match'<<ccr:'){throw "${id}: placeholder verify command."};
if($v.GetProperty('expect_exit_code').ValueKind-ne'Number'-or!$v.GetProperty('expect_exit_code').TryGetInt32([ref]$integer)-or$v.GetProperty('expect_exit_code').GetInt32()-lt-256-or$v.GetProperty('expect_exit_code').GetInt32()-gt255){throw "${id}: invalid verify exit code."}}}
foreach($t in $json.GetProperty('tasks').EnumerateArray()){foreach($dep in $t.GetProperty('depends_on').EnumerateArray()){if($dep.GetString()-notin$ids){throw "$($t.GetProperty('id').GetString()): unknown dependency."}}}
$planText=$raw;foreach($boundary in 'WinEventDebounce','WinEventMaxWait','VisiblePollInterval','MinimizedPollInterval','PopupRefreshInterval','Backoff','CacheWindowLimit','CacheTtl'){if($planText-notmatch[regex]::Escape($boundary)){throw "Missing explicit §9 boundary owner: $boundary."}}
if($EvidencePath){$e=Get-Content -Raw $EvidencePath|ConvertFrom-Json;
foreach($f in 'chrome_version','windows_build','dpi','language','cases'){if($null-eq$e.$f){throw "Evidence missing $f."}};
$map=if($RequireAcceptanceEvidence){@{'AC-05'=3;
'AC-06'=1;
'AC-07'=4;
'AC-08'=4;
'AC-09'=3;
'AC-10'=4;
'AC-11'=3;
'AC-12'=3}}else{@{'AC-01'=2;
'AC-02'=6;
'AC-03'=4;
'AC-04'=4}};
$expected=($map.Values|Measure-Object -Sum).Sum;
if(@($e.cases).Count-ne$expected){throw "Evidence requires $expected condition rows."};
if(@($e.cases.condition_id|Sort-Object -Unique).Count-ne$expected){throw 'Evidence condition IDs must be unique.'};
foreach($ac in $map.Keys){$rows=@($e.cases|Where-Object{$_.ac-eq$ac});
if($rows.Count-ne$map[$ac]){throw "$ac condition count invalid."};
foreach($row in $rows){foreach($f in 'condition_id','condition','window_state','expected_tabs','observed_tabs','duration_ms','result'){if($null-eq$row.$f){throw "$ac missing $f."}};
if($row.result -notin 'Pass','Fail','Blocked'){throw "$ac invalid result."};
if($row.result-eq'Pass'){if($row.expected_tabs-ne$row.observed_tabs){throw "$ac Pass counts differ."};
if($ac -in 'AC-01','AC-02'){foreach($f in 'restored','foregrounded','group_expanded'){if($row.read_side_effects.$f-ne$false){throw "$ac read side effect."}}};
if($ac -in 'AC-03','AC-04'){foreach($f in 'uia_selected','visual_foreground_tab','stale_token_rejected'){if($row.$f-ne$true){throw "$ac missing selection proof."}}}}}};
if($RequirePocEvidence-or$RequirePocGatePass){if($e.poc_gate -notin 'Pass','Fail','Blocked'){throw 'Invalid poc gate.'};
$allPass=@($e.cases|Where-Object{$_.result-ne'Pass'}).Count-eq0;
if($allPass -ne ($e.poc_gate-eq'Pass')){throw 'Gate does not match rows.'};
if($RequirePocGatePass-and!$allPass){throw 'PoC gate is not Pass.'}};
if($RequireAcceptanceEvidence){if($null-eq$e.out_of_scope_confirmed){throw 'Missing exclusions.'};
foreach($x in $e.out_of_scope_confirmed.psobject.Properties){if($x.Value-ne$false){throw "Exclusion $($x.Name) is present."}}}}
Write-Output "Validated $($p.tasks.Count) tasks: $($p.tasks.id -join ' -> ')"


