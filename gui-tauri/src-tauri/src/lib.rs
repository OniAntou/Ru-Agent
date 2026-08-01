use serde::Serialize;
use std::io;
use std::net::{IpAddr, Ipv4Addr, SocketAddr, TcpStream};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::Mutex;
use std::time::{Duration, Instant};
use tauri::{Manager, State};

#[cfg(windows)]
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle, RawHandle};

#[cfg(windows)]
use windows_sys::Win32::System::JobObjects::{
    AssignProcessToJobObject, CreateJobObjectW, JobObjectExtendedLimitInformation,
    SetInformationJobObject, JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
    JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
};

const BACKEND_ADDR: SocketAddr = SocketAddr::new(IpAddr::V4(Ipv4Addr::LOCALHOST), 8000);

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct BackendRuntimeConfig {
    base_url: String,
    token: String,
}

struct BackendState {
    runtime: BackendRuntimeConfig,
    child: Mutex<Option<Child>>,
    process_job: Mutex<Option<ProcessJob>>,
}

#[cfg(windows)]
struct ProcessJob {
    handle: OwnedHandle,
}

#[cfg(windows)]
impl ProcessJob {
    fn new() -> io::Result<Self> {
        let raw_handle = unsafe { CreateJobObjectW(std::ptr::null(), std::ptr::null()) };
        if raw_handle.is_null() {
            return Err(io::Error::last_os_error());
        }
        let handle = unsafe { OwnedHandle::from_raw_handle(raw_handle as RawHandle) };
        let mut limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        let configured = unsafe {
            SetInformationJobObject(
                handle.as_raw_handle() as _,
                JobObjectExtendedLimitInformation,
                std::ptr::from_ref(&limits).cast(),
                std::mem::size_of_val(&limits) as u32,
            )
        };
        if configured == 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(Self { handle })
    }

    fn assign(&self, child: &Child) -> io::Result<()> {
        let assigned = unsafe {
            AssignProcessToJobObject(self.handle.as_raw_handle() as _, child.as_raw_handle() as _)
        };
        if assigned == 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(())
    }
}

#[cfg(not(windows))]
struct ProcessJob;

#[cfg(not(windows))]
impl ProcessJob {
    fn new() -> io::Result<Self> {
        Ok(Self)
    }

    fn assign(&self, _child: &Child) -> io::Result<()> {
        Ok(())
    }
}

fn generate_token() -> String {
    let mut bytes = [0_u8; 32];
    getrandom::fill(&mut bytes).expect("operating system random source is unavailable");
    bytes.iter().map(|value| format!("{value:02x}")).collect()
}

fn project_root() -> io::Result<PathBuf> {
    if let Some(root) = std::env::var_os("RU_PROJECT_ROOT").map(PathBuf::from) {
        if root.join("src/mini_gpt/server.py").is_file() {
            return Ok(root);
        }
    }
    let manifest_root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .and_then(Path::parent)
        .map(Path::to_path_buf);
    if let Some(root) = manifest_root {
        if root.join("src/mini_gpt/server.py").is_file() {
            return Ok(root);
        }
    }
    Err(io::Error::new(
        io::ErrorKind::NotFound,
        "Ru project root not found; set RU_PROJECT_ROOT",
    ))
}

fn python_executable(root: &Path) -> io::Result<PathBuf> {
    let candidates = if cfg!(windows) {
        vec![
            root.join(".venv/Scripts/pythonw.exe"),
            root.join(".venv/Scripts/python.exe"),
        ]
    } else {
        vec![root.join(".venv/bin/python")]
    };
    candidates
        .into_iter()
        .find(|path| path.is_file())
        .ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::NotFound,
                "Ru Python environment was not found",
            )
        })
}

fn spawn_backend(root: &Path, token: &str) -> io::Result<Child> {
    if TcpStream::connect_timeout(&BACKEND_ADDR, Duration::from_millis(150)).is_ok() {
        return Err(io::Error::new(
            io::ErrorKind::AddrInUse,
            "port 8000 is already occupied; stop the unmanaged Ru backend first",
        ));
    }
    let python = python_executable(root)?;
    let mut command = Command::new(python);
    command
        .args([
            "-m",
            "uvicorn",
            "mini_gpt.server:app",
            "--host",
            "127.0.0.1",
            "--port",
            "8000",
        ])
        .current_dir(root)
        .env("PYTHONPATH", root.join("src"))
        .env("RU_DESKTOP_TOKEN", token)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(0x0800_0000);
    }
    command.spawn()
}

fn wait_until_ready(child: &mut Child) -> io::Result<()> {
    let deadline = Instant::now() + Duration::from_secs(20);
    while Instant::now() < deadline {
        if TcpStream::connect_timeout(&BACKEND_ADDR, Duration::from_millis(150)).is_ok() {
            return Ok(());
        }
        if let Some(status) = child.try_wait()? {
            return Err(io::Error::other(format!(
                "Ru backend exited before becoming ready: {status}"
            )));
        }
        std::thread::sleep(Duration::from_millis(100));
    }
    Err(io::Error::new(
        io::ErrorKind::TimedOut,
        "Ru backend did not become ready within 20 seconds",
    ))
}

fn start_backend_state() -> io::Result<BackendState> {
    let token = generate_token();
    let root = project_root()?;
    let process_job = ProcessJob::new()?;
    let mut child = spawn_backend(&root, &token)?;
    if let Err(error) = process_job.assign(&child) {
        let _ = child.kill();
        let _ = child.wait();
        return Err(error);
    }
    if let Err(error) = wait_until_ready(&mut child) {
        drop(process_job);
        let _ = child.wait();
        return Err(error);
    }
    Ok(BackendState {
        runtime: BackendRuntimeConfig {
            base_url: "http://127.0.0.1:8000".to_owned(),
            token,
        },
        child: Mutex::new(Some(child)),
        process_job: Mutex::new(Some(process_job)),
    })
}

#[tauri::command]
fn backend_runtime(state: State<'_, BackendState>) -> BackendRuntimeConfig {
    state.runtime.clone()
}

fn stop_owned_backend(state: &BackendState) {
    if let Ok(mut guard) = state.process_job.lock() {
        guard.take();
    }
    let Ok(mut guard) = state.child.lock() else {
        return;
    };
    if let Some(mut child) = guard.take() {
        let _ = child.kill();
        let _ = child.wait();
    }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let backend = start_backend_state().expect("could not start the owned Ru backend");
    let app = tauri::Builder::default()
        .manage(backend)
        .invoke_handler(tauri::generate_handler![backend_runtime])
        .build(tauri::generate_context!())
        .expect("error while building Ru desktop");
    app.run(|handle, event| {
        if matches!(
            event,
            tauri::RunEvent::Exit | tauri::RunEvent::ExitRequested { .. }
        ) {
            stop_owned_backend(handle.state::<BackendState>().inner());
        }
    });
}

#[cfg(test)]
mod tests {
    use super::generate_token;

    #[cfg(windows)]
    use std::fs;
    #[cfg(windows)]
    use std::process::{Command, Stdio};
    #[cfg(windows)]
    use std::thread;
    #[cfg(windows)]
    use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

    #[test]
    fn runtime_token_is_long_and_unpredictable_enough_for_loopback_auth() {
        let first = generate_token();
        let second = generate_token();
        assert!(first.len() >= 48);
        assert_ne!(first, second);
        assert!(first.bytes().all(|value| value.is_ascii_alphanumeric()));
    }

    #[cfg(windows)]
    fn process_exists(pid: u32) -> bool {
        let output = Command::new("tasklist.exe")
            .args(["/FI", &format!("PID eq {pid}"), "/FO", "CSV", "/NH"])
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .output()
            .expect("tasklist should be available on Windows");
        String::from_utf8_lossy(&output.stdout).contains(&format!("\"{pid}\""))
    }

    #[cfg(windows)]
    #[test]
    fn dropping_process_job_kills_descendant_tree() {
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system time should be after Unix epoch")
            .as_nanos();
        let temp_dir = std::env::temp_dir().join(format!(
            "ru-process-job-test-{}-{nonce}",
            std::process::id()
        ));
        fs::create_dir(&temp_dir).expect("test temp directory should be created");
        let gate_path = temp_dir.join("start");
        let pid_path = temp_dir.join("child.pid");
        let script = concat!(
            "$deadline=[DateTime]::UtcNow.AddSeconds(10); ",
            "while(-not (Test-Path -LiteralPath $env:RU_JOB_GATE)){ ",
            "if([DateTime]::UtcNow -gt $deadline){exit 10}; ",
            "Start-Sleep -Milliseconds 25 }; ",
            "$child=Start-Process -FilePath ping.exe ",
            "-ArgumentList '127.0.0.1','-n','60' -WindowStyle Hidden -PassThru; ",
            "[IO.File]::WriteAllText($env:RU_CHILD_PID_FILE,[string]$child.Id); ",
            "Wait-Process -Id $child.Id"
        );
        let mut parent = Command::new("powershell.exe")
            .args(["-NoProfile", "-NonInteractive", "-Command", script])
            .env("RU_JOB_GATE", &gate_path)
            .env("RU_CHILD_PID_FILE", &pid_path)
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .expect("test parent process should start");

        let job = super::ProcessJob::new().expect("kill-on-close job should be created");
        job.assign(&parent)
            .expect("test parent should be assigned to the job");
        fs::write(&gate_path, b"go").expect("test gate should open");

        let deadline = Instant::now() + Duration::from_secs(10);
        while !pid_path.is_file() && Instant::now() < deadline {
            thread::sleep(Duration::from_millis(25));
        }
        let descendant_pid: u32 = fs::read_to_string(&pid_path)
            .expect("descendant pid should be written")
            .trim()
            .parse()
            .expect("descendant pid should be numeric");
        assert!(process_exists(descendant_pid));

        drop(job);
        let deadline = Instant::now() + Duration::from_secs(5);
        while process_exists(descendant_pid) && Instant::now() < deadline {
            thread::sleep(Duration::from_millis(50));
        }
        let descendant_survived = process_exists(descendant_pid);

        let _ = parent.kill();
        let _ = parent.wait();
        let _ = fs::remove_dir_all(&temp_dir);
        assert!(
            !descendant_survived,
            "closing the job must terminate backend descendants"
        );
    }
}
