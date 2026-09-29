[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$LabRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RuntimeRoot = Join-Path $LabRoot 'runtime'
$PythonRoot = Join-Path $RuntimeRoot 'python'
$PythonExe = Join-Path $PythonRoot 'python.exe'
$VenvRoot = Join-Path $RuntimeRoot 'venv'
$VenvPython = Join-Path $VenvRoot 'Scripts\python.exe'
$ModelsRoot = Join-Path $RuntimeRoot 'models'
$CacheRoot = Join-Path $RuntimeRoot 'cache'
$LogsRoot = Join-Path $RuntimeRoot 'logs'
$LogPath = Join-Path $LogsRoot 'setup-ocr-runtime.log'
$Installer = Join-Path $CacheRoot 'python-3.11.9-amd64.exe'
$PythonUrl = 'https://www.python.org/ftp/python/3.11.9/python-3.11.9-amd64.exe'
$Stage = 'initializing'

New-Item -ItemType Directory -Force -Path $RuntimeRoot,$ModelsRoot,$CacheRoot,$LogsRoot | Out-Null
[IO.File]::AppendAllText($LogPath, "`r`n==== setup started $(Get-Date -Format o) ==== `r`n", [Text.UTF8Encoding]::new($false))

function Write-Log([string]$Text) {
    [IO.File]::AppendAllText($LogPath, $Text + "`r`n", [Text.UTF8Encoding]::new($false))
}

function Write-Stage([string]$Number, [string]$Text) {
    $script:Stage = $Text
    Write-Host "`n[$Number/6] $Text" -ForegroundColor Cyan
    Write-Log "[$Number/6] $Text"
}

function ConvertTo-NativeArgument([string]$Value) {
    if ($Value -notmatch '[\s"]') { return $Value }
    return '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

function Invoke-NativeProcess([string]$FilePath, [string[]]$Arguments, [string]$Label) {
    $id = [Guid]::NewGuid().ToString('N')
    $stdoutPath = Join-Path $LogsRoot ("native-$id.stdout.log")
    $stderrPath = Join-Path $LogsRoot ("native-$id.stderr.log")
    $argumentLine = (($Arguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' ')
    Write-Log "BEGIN $Label"
    Write-Log "FILE $FilePath"
    Write-Log "ARGS $argumentLine"
    try {
        $process = Start-Process -FilePath $FilePath -ArgumentList $argumentLine -Wait -PassThru -NoNewWindow `
            -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
        $stdout = if (Test-Path $stdoutPath) { [IO.File]::ReadAllText($stdoutPath) } else { '' }
        $stderr = if (Test-Path $stderrPath) { [IO.File]::ReadAllText($stderrPath) } else { '' }
        if ($stdout) { Write-Log "STDOUT`r`n$stdout" }
        if ($stderr) { Write-Log "STDERR`r`n$stderr" }
        Write-Log "END $Label exit=$($process.ExitCode)"
        $result = [pscustomobject]@{ ExitCode = $process.ExitCode; StdOut = $stdout; StdErr = $stderr }
        if ($process.ExitCode -ne 0) {
            $tail = (($stderr + "`r`n" + $stdout) -split "`r?`n" | Select-Object -Last 12) -join "`n"
            throw "$Label failed with exit code $($process.ExitCode).`n$tail"
        }
        return $result
    }
    finally {
        Remove-Item -LiteralPath $stdoutPath,$stderrPath -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-Python([string[]]$Arguments, [string]$Label = 'python') {
    return Invoke-NativeProcess -FilePath $VenvPython -Arguments $Arguments -Label $Label
}

function Test-PythonImports([string[]]$Modules, [string]$Label) {
    try {
        $code = 'import ' + ($Modules -join ',')
        [void](Invoke-Python @('-c',$code) $Label)
        return $true
    } catch {
        Write-Log "$Label not ready: $($_.Exception.Message)"
        return $false
    }
}

function Test-Python311X64([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    try {
        $result = Invoke-NativeProcess -FilePath $Path -Arguments @('-c',"import platform,sys; print(str(sys.version_info.major)+'.'+str(sys.version_info.minor)+'|'+platform.architecture()[0])") -Label 'validate Python 3.11 x64'
        return $result.StdOut.Trim() -eq '3.11|64bit'
    } catch { return $false }
}

function Ensure-DirectoryLink([string]$LinkPath, [string]$TargetPath) {
    New-Item -ItemType Directory -Force -Path $TargetPath | Out-Null
    if (Test-Path -LiteralPath $LinkPath) {
        $item = Get-Item -LiteralPath $LinkPath -Force
        if ($item.LinkType -eq 'Junction' -and $item.Target -contains $TargetPath) { return }
        $children = @(Get-ChildItem -LiteralPath $LinkPath -Force -ErrorAction SilentlyContinue)
        if ($children.Count -gt 0) { throw "Cannot create isolated model link because this directory is not empty: $LinkPath" }
        Remove-Item -LiteralPath $LinkPath -Force
    }
    New-Item -ItemType Junction -Path $LinkPath -Target $TargetPath | Out-Null
}

try {
    Write-Stage 1 'Preparing Python'
    if (-not (Test-Python311X64 $PythonExe)) {
        if (-not (Test-Path -LiteralPath $Installer)) {
            Write-Host "Downloading official Python 3.11 x64 from python.org..."
            Invoke-WebRequest -Uri $PythonUrl -OutFile $Installer -UseBasicParsing
        }
        $signature = Get-AuthenticodeSignature -LiteralPath $Installer
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Python Software Foundation') {
            throw "Python installer signature is not valid or is not signed by the Python Software Foundation: $($signature.Status)"
        }
        $installerArgs = @('/quiet','InstallAllUsers=0','PrependPath=0','AppendPath=0','Include_launcher=0','InstallLauncherAllUsers=0','Include_test=0','Include_doc=0',"TargetDir=$PythonRoot")
        [void](Invoke-NativeProcess -FilePath $Installer -Arguments $installerArgs -Label 'official Python installer')
    }
    if (-not (Test-Python311X64 $PythonExe)) { throw "A working Python 3.11 x64 was not found at $PythonExe" }
    $baseVersion = Invoke-NativeProcess -FilePath $PythonExe -Arguments @('--version') -Label 'runtime Python --version'
    Write-Host ("Runtime Python: " + ($baseVersion.StdOut + $baseVersion.StdErr).Trim())

    Write-Stage 2 'Creating isolated environment'
    if (-not (Test-Path -LiteralPath $VenvPython)) {
        [void](Invoke-NativeProcess -FilePath $PythonExe -Arguments @('-m','venv',$VenvRoot) -Label 'create isolated venv')
    }
    if (-not (Test-Path -LiteralPath $VenvPython)) { throw 'The isolated virtual environment was not created.' }
    $venvVersion = Invoke-NativeProcess -FilePath $VenvPython -Arguments @('--version') -Label 'venv Python --version'
    $pipVersion = Invoke-Python @('-m','pip','--version') 'venv pip --version'
    Write-Host ("Venv Python: " + ($venvVersion.StdOut + $venvVersion.StdErr).Trim())
    Write-Host ("Venv pip: " + ($pipVersion.StdOut + $pipVersion.StdErr).Trim())
    $pipMajorText = Invoke-Python @('-c','import pip; print(pip.__version__.split(".")[0])') 'inspect pip version'
    $pipMajor = 0
    [void][int]::TryParse($pipMajorText.StdOut.Trim(), [ref]$pipMajor)
    if ($pipMajor -lt 24) {
        [void](Invoke-Python @('-m','pip','install','--upgrade','pip','setuptools','wheel') 'upgrade pip tooling')
    } else {
        Write-Host 'Existing pip toolchain is usable; resume without reinstalling it.'
        Write-Log "pip toolchain reused (major=$pipMajor)"
    }

    $env:PADDLEX_HOME = Join-Path $ModelsRoot 'paddlex-home'
    $env:HF_HOME = Join-Path $CacheRoot 'huggingface-home'
    $env:PADDLE_PDX_MODEL_SOURCE = 'BOS'
    $env:RAPIDOCR_HOME = Join-Path $ModelsRoot 'rapidocr-home'
    $env:CUDA_VISIBLE_DEVICES = '-1'
    $env:FLAGS_selected_gpus = ''
    New-Item -ItemType Directory -Force -Path $env:PADDLEX_HOME,$env:HF_HOME,$env:RAPIDOCR_HOME | Out-Null

    # The current lab executable redirects Paddle caches through LabRoot\models.
    # Junctions keep the real files inside runtime while preserving that interface.
    $LabModels = Join-Path $LabRoot 'models'
    New-Item -ItemType Directory -Force -Path $LabModels | Out-Null
    Ensure-DirectoryLink (Join-Path $LabModels 'paddlex-home') $env:PADDLEX_HOME
    Ensure-DirectoryLink (Join-Path $LabModels 'huggingface-home') $env:HF_HOME

    Write-Stage 3 'Installing PaddleOCR'
    if (Test-PythonImports @('paddle','paddleocr','paddlex') 'check existing Paddle runtime') {
        Write-Host 'Existing PaddlePaddle/PaddleOCR/PaddleX installation will be reused.'
    } else {
        [void](Invoke-Python @('-m','pip','install','--only-binary=:all:','paddlepaddle') 'install PaddlePaddle CPU')
        [void](Invoke-Python @('-m','pip','install','paddleocr==3.7.0','psutil') 'install PaddleOCR')
    }

    Write-Stage 4 'Installing RapidOCR'
    if (Test-PythonImports @('rapidocr','onnxruntime') 'check existing RapidOCR runtime') {
        Write-Host 'Existing RapidOCR/ONNX Runtime installation will be reused.'
    } else {
        [void](Invoke-Python @('-m','pip','install','rapidocr>=3.9.0','onnxruntime','psutil') 'install RapidOCR and ONNX Runtime CPU')
    }
    $freeze = Invoke-Python @('-m','pip','freeze') 'capture requirements lock'
    [IO.File]::WriteAllText((Join-Path $RuntimeRoot 'requirements-lock.txt'), $freeze.StdOut, [Text.UTF8Encoding]::new($false))

    Write-Stage 5 'Preparing models'
    $Sample = Get-ChildItem -LiteralPath (Join-Path $LabRoot 'tests\ocr-samples') -File -ErrorAction Stop |
        Where-Object { $_.Extension -match '^\.(png|jpg|jpeg|bmp)$' } | Select-Object -First 1
    if (-not $Sample) { throw 'No verification image exists under tests\ocr-samples.' }

    $VerifyScript = Join-Path $RuntimeRoot 'verify_ocr_runtime.py'
    @'
import importlib.metadata, json, os, pathlib, platform, sys, time

engine_name, sample, result_path = sys.argv[1], sys.argv[2], sys.argv[3]
summary = {"engine": engine_name, "status": "FAILED", "sample": sample,
           "python": sys.version.split()[0], "architecture": platform.architecture()[0]}

import numpy, cv2
summary["versions"] = {
    "numpy": numpy.__version__, "opencv": cv2.__version__
}

if engine_name == "paddle":
    import paddle, paddleocr, paddlex
    summary["versions"].update({"paddlepaddle": paddle.__version__,
        "paddleocr": getattr(paddleocr,"__version__","unknown"),
        "paddlex": getattr(paddlex,"__version__","unknown")})
    summary["paddle_device"] = paddle.device.get_device()
    if not summary["paddle_device"].startswith("cpu"):
        raise RuntimeError("Paddle did not initialize on CPU: " + summary["paddle_device"])
    from paddleocr import PaddleOCR
    t0=time.perf_counter()
    ocr=PaddleOCR(text_detection_model_name="PP-OCRv6_medium_det",
        text_recognition_model_name="PP-OCRv6_medium_rec", use_doc_orientation_classify=False,
        use_doc_unwarping=False, use_textline_orientation=False, device="cpu", enable_mkldnn=False)
    summary["model_load_ms"]=round((time.perf_counter()-t0)*1000)
    t0=time.perf_counter(); output=list(ocr.predict(sample))
    summary["ocr_ms"]=round((time.perf_counter()-t0)*1000)
    summary["parseable"]=isinstance(output,list); summary["result_items"]=len(output)
    summary["cpu_mode"]="oneDNN disabled"
elif engine_name == "rapid":
    import rapidocr, onnxruntime
    summary["versions"].update({"rapidocr": importlib.metadata.version("rapidocr"),
        "onnxruntime": onnxruntime.__version__})
    from rapidocr import RapidOCR
    t0=time.perf_counter()
    ocr=RapidOCR()
    summary["model_load_ms"]=round((time.perf_counter()-t0)*1000)
    t0=time.perf_counter(); output=ocr(sample)
    summary["ocr_ms"]=round((time.perf_counter()-t0)*1000)
    summary["parseable"]=output is not None
    texts=getattr(output,"txts",None); summary["result_items"]=0 if texts is None else len(texts)
    summary["cpu_mode"]="ONNX Runtime CPU"
else:
    raise ValueError("unknown engine: " + engine_name)

summary["status"]="READY" if summary["parseable"] else "FAILED"

root=pathlib.Path(os.environ["OCR_RUNTIME_ROOT"])
models=[]
for base in (root/"models", root/"cache", root/"venv"):
    if not base.exists(): continue
    for p in base.rglob("*"):
        if p.is_file() and (p.suffix.lower() in {".onnx",".pdmodel",".pdiparams",".json",".yaml",".yml"} or "model" in p.name.lower()):
            models.append({"path":str(p),"bytes":p.stat().st_size})
summary["model_files"]=models
summary["model_total_bytes"]=sum(x["bytes"] for x in models)
payload=json.dumps(summary,ensure_ascii=False,indent=2)
pathlib.Path(result_path).write_text(payload,encoding="utf-8")
print("OCR verification completed: " + result_path)
'@ | Set-Content -LiteralPath $VerifyScript -Encoding UTF8

    Write-Stage 6 'Verifying OCR engines'
    $env:OCR_RUNTIME_ROOT = $RuntimeRoot
    $PaddleVerificationPath = Join-Path $RuntimeRoot 'verification-paddle.json'
    $RapidVerificationPath = Join-Path $RuntimeRoot 'verification-rapid.json'
    Remove-Item -LiteralPath $PaddleVerificationPath,$RapidVerificationPath -Force -ErrorAction SilentlyContinue
    $paddleError = ''; $rapidError = ''
    try { [void](Invoke-Python @($VerifyScript,'paddle',$Sample.FullName,$PaddleVerificationPath) 'verify PaddleOCR (oneDNN disabled)') }
    catch { $paddleError = $_.Exception.Message }
    try { [void](Invoke-Python @($VerifyScript,'rapid',$Sample.FullName,$RapidVerificationPath) 'verify RapidOCR independently') }
    catch { $rapidError = $_.Exception.Message }
    $paddleVerification = if (Test-Path $PaddleVerificationPath) { Get-Content $PaddleVerificationPath -Raw | ConvertFrom-Json } else { $null }
    $rapidVerification = if (Test-Path $RapidVerificationPath) { Get-Content $RapidVerificationPath -Raw | ConvertFrom-Json } else { $null }
    $paddleReady = $paddleVerification -and $paddleVerification.status -eq 'READY'
    $rapidReady = $rapidVerification -and $rapidVerification.status -eq 'READY'
    Write-Host ("PaddleOCR: " + $(if($paddleReady){'READY'}else{'FAILED'})) -ForegroundColor $(if($paddleReady){'Green'}else{'Red'})
    Write-Host ("RapidOCR: " + $(if($rapidReady){'READY'}else{'FAILED'})) -ForegroundColor $(if($rapidReady){'Green'}else{'Red'})
    Write-Log "PaddleOCR ready=$paddleReady error=$paddleError"
    Write-Log "RapidOCR ready=$rapidReady error=$rapidError"
    if (-not $paddleReady -or -not $rapidReady) {
        throw "OCR verification incomplete. PaddleOCR=$(if($paddleReady){'READY'}else{'FAILED'}); RapidOCR=$(if($rapidReady){'READY'}else{'FAILED'}). See the per-engine errors above and $LogPath"
    }

    $SettingsPath = Join-Path $LabRoot 'lab-settings.json'
    @{ PythonPath = $VenvPython } | ConvertTo-Json | Set-Content -LiteralPath $SettingsPath -Encoding UTF8

    Write-Host "`nOCR runtime setup completed successfully." -ForegroundColor Green
    Write-Host "Python: $VenvPython"
    Write-Host "Models/cache: $ModelsRoot ; $CacheRoot"
    Write-Host "Paddle verification: $PaddleVerificationPath"
    Write-Host "Rapid verification: $RapidVerificationPath"
    Write-Host "Lock file: $(Join-Path $RuntimeRoot 'requirements-lock.txt')"
    Write-Host "Log: $LogPath"
    exit 0
}
catch {
    Write-Host "`nOCR runtime setup FAILED during: $Stage" -ForegroundColor Red
    Write-Host $_.Exception.ToString() -ForegroundColor Red
    Write-Host "Log: $LogPath" -ForegroundColor Yellow
    Write-Log ("FAILED stage=" + $Stage + "`r`n" + $_.Exception.ToString())
    if ($Host.Name -notmatch 'ServerRemoteHost') { [void](Read-Host 'Press Enter to close') }
    exit 1
}
