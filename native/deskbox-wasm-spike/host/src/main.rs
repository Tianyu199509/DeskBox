// Leg-3 spike host (roadmap stage 3.5): Wasmtime component-model embedding
// for deskbox:plugin worlds. Same behavior and permission semantics as the
// other two legs: every guest capability import goes through the
// requested AND in-scope AND granted gate, redirects are refused, response
// bodies are capped - the sandbox never replaces the policy, it adds to it.
//
// Governance: deterministic fuel budget + epoch deadline + a store memory
// limit, so a hostile guest (busy loop, memory ballooning) is interrupted
// and reported, never fatal to the host.
//
// Usage: deskbox-wasm-spike-host <pkgDir>
//   [--grant=<permissionId>=<host>]...
//   [--self-test=ok|redirect|server-error|evil-ask|spin]
//   [--invoke-widget=<contributionId>] [--measure]
use std::io::Read;
use std::net::TcpListener;
use std::sync::Mutex;
use std::time::Instant;

use wasmtime::component::{bindgen, Linker};
use wasmtime::{Config, Engine, ResourceLimiter, Store};

bindgen!({ path: "../wit", world: "plugin-world" });

use deskbox::plugin::capabilities::FetchResult;

const FUEL_LIMIT: u64 = 2_000_000;
const MEMORY_LIMIT: usize = 64 * 1024 * 1024;
const MAX_RESPONSE_BYTES: usize = 2 * 1024 * 1024;
const EPOCH_TICK_MS: u64 = 10;

struct HostState {
    manifest: serde_json::Value,
    grants: Vec<String>,
    self_test: Option<String>,
    mock_url: Option<String>,
    widget_states: Mutex<Vec<serde_json::Value>>,
    capability_calls: Mutex<Vec<String>>,
    refusals: Mutex<Vec<String>>,
    invocation: Mutex<Option<serde_json::Value>>,
    limiter: Limiter,
}

impl HostState {
    /// Canonical URL check for the capability gate: a REAL parser (never a
    /// hand-rolled split - userinfo/IPv6/encoded forms parse differently
    /// and a bypass is "gate sees host A, HTTP client connects to B").
    /// Production policy is HTTPS-only; the loopback exemption exists
    /// solely for the self-test mock server.
    fn canonical_host(url: &str) -> Result<(String, bool), String> {
        let parsed = url::Url::parse(url).map_err(|e| format!("unparseable url '{url}': {e}"))?;
        let host = parsed
            .host_str()
            .ok_or_else(|| format!("url '{url}' has no host"))?
            .to_ascii_lowercase();
        Ok((host, parsed.scheme() == "https"))
    }

    fn gate(&self, url: &str, permission: &str) -> Result<(), String> {
        let permissions = self
            .manifest
            .get("permissions")
            .and_then(|p| p.as_array())
            .cloned()
            .unwrap_or_default();
        let declared = permissions
            .iter()
            .any(|p| p.get("id").and_then(|i| i.as_str()) == Some(permission));
        if !declared {
            return Err(format!("capability '{permission}' is not declared by the package"));
        }
        let (host, is_https) = Self::canonical_host(url)?;
        let loopback_mock = self.mock_url.is_some()
            && !is_https
            && (host == "127.0.0.1" || host == "localhost" || host == "[::1]");
        if !is_https && !loopback_mock {
            return Err(format!(
                "url '{url}' must use https (scheme enforcement; http requests are refused)"
            ));
        }
        let in_scope = permissions
            .iter()
            .find(|p| p.get("id").and_then(|i| i.as_str()) == Some(permission))
            .and_then(|p| p.get("scope"))
            .and_then(|s| s.get("allow"))
            .and_then(|a| a.as_array())
            .map(|entries| {
                entries
                    .iter()
                    .any(|e| e.as_str().map(|s| s.to_ascii_lowercase()) == Some(host.clone()))
            })
            .unwrap_or(false);
        if !in_scope {
            return Err(format!("url '{url}' is outside the declared {permission} scope"));
        }
        let granted = self
            .grants
            .iter()
            .any(|g| g.eq_ignore_ascii_case(&format!("{permission}={host}")));
        if !granted {
            return Err(format!(
                "url host '{host}' has no granted {permission} capability (requested != granted)"
            ));
        }
        Ok(())
    }

    fn fetch(&self, url: &str) -> Result<(u16, String), String> {
        let agent = ureq::AgentBuilder::new()
            .redirects(0) // redirects are refused, never followed
            .timeout(std::time::Duration::from_secs(10))
            .build();
        let response = agent
            .get(url)
            .set("User-Agent", "DeskBox-spike")
            .set("Accept", "application/json")
            .call()
            .map_err(|e| match &e {
                ureq::Error::Status(code, _) if (300..400).contains(code) => format!(
                    "url '{url}' attempted a redirect; redirects are refused in v0.3 instead of followed"
                ),
                ureq::Error::Status(code, _) => format!("HTTP {code}"),
                other => other.to_string(),
            })?;
        // ureq with redirects(0) returns 3xx responses as Ok - the redirect
        // must still be refused here, never followed.
        let status = response.status();
        if (300..400).contains(&status) {
            return Err(format!(
                "url '{url}' attempted a redirect; redirects are refused in v0.3 instead of followed"
            ));
        }
        if !(200..300).contains(&status) {
            return Err(format!("HTTP {status}"));
        }
        let mut body = String::new();
        let mut reader = response.into_reader().take((MAX_RESPONSE_BYTES + 1) as u64);
        reader
            .read_to_string(&mut body)
            .map_err(|e| e.to_string())?;
        if body.len() > MAX_RESPONSE_BYTES {
            return Err(format!(
                "response exceeds the {MAX_RESPONSE_BYTES}-byte host limit"
            ));
        }
        Ok((200, body))
    }
}

impl deskbox::plugin::capabilities::Host for HostState {
    fn network_fetch(&mut self, url: String) -> Result<FetchResult, String> {
        self.capability_calls.lock().unwrap().push("network.fetch".into());
        let effective_url = if let Some(mock) = &self.mock_url {
            mock.clone()
        } else if self.self_test.as_deref() == Some("evil-ask") {
            "https://evil.example/secret".to_string()
        } else {
            url
        };
        self.gate(&effective_url, "network.fetch").map_err(|refused| {
            self.refusals.lock().unwrap().push(refused.clone());
            refused
        }).and_then(|()| {
            // Redirect refusal is transport-level: redirects(0) surfaces a
            // 3xx as a status error, which we translate to the policy
            // refusal message.
            self.fetch(&effective_url)
                .map(|(status, body)| FetchResult { status, body })
        })
    }

    fn shell_open(&mut self, url: String) -> Result<bool, String> {
        self.capability_calls.lock().unwrap().push("shell.open".into());
        match self.gate(&url, "shell.open") {
            Ok(()) => {
                *self.invocation.lock().unwrap() = Some(serde_json::json!({
                    "type": "open-url",
                    "url": url,
                    "note": "spike harness prints intent"
                }));
                Ok(false)
            }
            Err(refused) => {
                self.refusals.lock().unwrap().push(refused.clone());
                Err(refused)
            }
        }
    }

    fn widget_update(
        &mut self,
        contribution_id: String,
        value: String,
        note: Option<String>,
    ) {
        self.widget_states.lock().unwrap().push(serde_json::json!({
            "contributionId": contribution_id,
            "value": value,
            "note": note,
        }));
    }
}

struct Limiter;
impl ResourceLimiter for Limiter {
    fn memory_growing(
        &mut self,
        current: usize,
        desired: usize,
        _maximum: Option<usize>,
    ) -> wasmtime::Result<bool> {
        Ok(desired <= MEMORY_LIMIT.max(current))
    }
    fn table_growing(
        &mut self,
        current: usize,
        desired: usize,
        _maximum: Option<usize>,
    ) -> wasmtime::Result<bool> {
        Ok(desired <= current + 1000)
    }
}

fn main() {
    if let Err(error) = run() {
        eprintln!("FAILED: {error}");
        std::process::exit(1);
    }
}

fn run() -> anyhow::Result<()> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let pkg_dir = args
        .first()
        .cloned()
        .unwrap_or_else(|| "spikes/github-stats-wasm".into());
    let grants: Vec<String> = args
        .iter()
        .filter_map(|a| a.strip_prefix("--grant="))
        .map(|s| s.to_string())
        .collect();
    let self_test = args
        .iter()
        .find_map(|a| a.strip_prefix("--self-test=").map(|s| s.to_string()));
    let invoke_widget = args
        .iter()
        .find_map(|a| a.strip_prefix("--invoke-widget=").map(|s| s.to_string()));
    let measure = args.iter().any(|a| a == "--measure");

    let manifest_path = std::path::Path::new(&pkg_dir).join("manifest.json");
    let manifest: serde_json::Value = serde_json::from_str(&std::fs::read_to_string(&manifest_path)?)?;
    if manifest.get("runtime").and_then(|r| r.as_str()) != Some("wasm") {
        anyhow::bail!("package is not a wasm plugin (runtime \"wasm\" required)");
    }
    let entry = manifest
        .get("entry")
        .and_then(|e| e.get("main"))
        .and_then(|m| m.as_str())
        .ok_or_else(|| anyhow::anyhow!("entry.main required"))?
        .to_string();
    let mut manifest = manifest;

    // Self-test mock server (hand-rolled HTTP/1.1, no dependencies).
    let mock_url = match self_test.as_deref() {
        Some(mode) if ["ok", "redirect", "server-error"].contains(&mode) => {
            Some(start_mock_server(mode)?)
        }
        _ => None,
    };
    if mock_url.is_some() {
        if let Some(permissions) = manifest.get_mut("permissions").and_then(|p| p.as_array_mut()) {
            for permission in permissions.iter_mut() {
                if permission.get("id").and_then(|i| i.as_str()) == Some("network.fetch") {
                    if let Some(scope) = permission.get_mut("scope").and_then(|s| s.get_mut("allow")).and_then(|a| a.as_array_mut()) {
                        scope.push(serde_json::Value::String("127.0.0.1".into()));
                    }
                }
            }
        }
    }

    let state = HostState {
        manifest: manifest.clone(),
        grants,
        self_test: self_test.clone(),
        mock_url,
        widget_states: Mutex::new(Vec::new()),
        capability_calls: Mutex::new(Vec::new()),
        refusals: Mutex::new(Vec::new()),
        invocation: Mutex::new(None),
        limiter: Limiter,
    };

    // ---------- engine: fuel + epoch + memory limit ----------
    let mut config = Config::new();
    config.consume_fuel(true);
    config.epoch_interruption(true);
    config.wasm_component_model(true);
    let engine = Engine::new(&config)?;

    let _ticker = std::thread::spawn({
        let engine = engine.clone();
        move || loop {
            engine.increment_epoch();
            std::thread::sleep(std::time::Duration::from_millis(EPOCH_TICK_MS));
        }
    });

    let component_bytes = std::fs::read(std::path::Path::new(&pkg_dir).join(entry))?;
    let compile_start = Instant::now();
    let component = wasmtime::component::Component::new(&engine, &component_bytes)?;
    let compile_ms = compile_start.elapsed().as_millis();

    let mut store = Store::new(&engine, state);
    store.set_fuel(FUEL_LIMIT)?;
    // Epoch interruption is the wall-clock backstop (the deterministic
    // budget is fuel): trap after N epoch ticks.
    store.epoch_deadline_trap();
    store.set_epoch_deadline(60_000);
    store.limiter(|state: &mut HostState| &mut state.limiter as &mut dyn ResourceLimiter);

    let mut linker: Linker<HostState> = Linker::new(&engine);
    PluginWorld::add_to_linker::<HostState, wasmtime::component::HasSelf<HostState>>(
        &mut linker,
        |state: &mut HostState| state,
    )?;

    let instantiate_start = Instant::now();
    let world = PluginWorld::instantiate(&mut store, &component, &mut linker)?;
    let instantiate_ms = instantiate_start.elapsed().as_millis();

    // ---------- activate ----------
    let reason = if self_test.as_deref() == Some("spin") {
        "spin".to_string()
    } else {
        "startup".to_string()
    };
    let activate_start = Instant::now();
    let activate_result = world.call_activate(&mut store, &reason);
    let activate_ms = activate_start.elapsed().as_millis();
    if let Err(error) = activate_result {
        let message = format_trap(&error);
        eprintln!("FAILED: {message}");
        std::process::exit(1);
    }

    let mut invocation_json = None;
    if let Some(widget_id) = invoke_widget {
        let action_id = manifest
            .get("contributions")
            .and_then(|c| c.as_array())
            .and_then(|cs| {
                cs.iter().find(|c| c.get("id").and_then(|i| i.as_str()) == Some(widget_id.as_str()))
            })
            .and_then(|c| c.get("payload"))
            .and_then(|p| p.get("primaryActionId"))
            .and_then(|a| a.as_str())
            .ok_or_else(|| anyhow::anyhow!("widget '{widget_id}' declares no primaryActionId"))?;
        world.call_invoke_action(&mut store, action_id)?;
        invocation_json = store.data().invocation.lock().unwrap().clone();
    }
    let states = store.data().widget_states.lock().unwrap().clone();
    let calls = store.data().capability_calls.lock().unwrap().clone();
    let refusals = store.data().refusals.lock().unwrap().clone();
    let invocation_value = invocation_json
        .or_else(|| store.data().invocation.lock().unwrap().clone());

    let mut output = serde_json::json!({
        "package": manifest.get("id").cloned().unwrap_or(serde_json::Value::Null),
        "widgetStates": states,
        "invocation": invocation_value,
        "capabilityCalls": calls,
    });
    if !refusals.is_empty() {
        output["refusals"] = serde_json::json!(refusals);
    }
    if measure {
        output["measurements"] = serde_json::json!({
            // Cold start = compile + instantiate + activate. The earlier
            // "instantiate ~0ms" claim excluded compilation (round-8 fix);
            // instantiateMs alone is a warm-cache-style number.
            "compileMs": compile_ms,
            "instantiateMs": instantiate_ms,
            "activateMs": activate_ms,
            "fuelRemaining": store.get_fuel().ok(),
        });
    }
    println!("{}", serde_json::to_string_pretty(&output)?);

    // The epoch ticker thread never stops by design; a CLI harness exits
    // explicitly instead of joining it.
    std::process::exit(0);
}

fn format_trap(error: &wasmtime::Error) -> String {
    let chain: Vec<String> = error
        .chain()
        .map(|e| e.to_string())
        .collect();
    let joined = chain.join("; ");
    if joined.contains("all fuel consumed") {
        "plugin exhausted its fuel budget (interrupted)".to_string()
    } else if joined.contains("epoch") {
        "plugin exceeded its epoch deadline (interrupted)".to_string()
    } else {
        joined
    }
}

fn start_mock_server(mode: &str) -> anyhow::Result<String> {
    let listener = TcpListener::bind("127.0.0.1:0")?;
    let port = listener.local_addr()?.port();
    let mode = mode.to_string();
    std::thread::spawn(move || {
        for stream in listener.incoming() {
            let Ok(mut stream) = stream else { continue };
            let mut buf = [0u8; 1024];
            let _ = std::io::Read::read(&mut stream, &mut buf);
            let response = match mode.as_str() {
                "redirect" => {
                    "HTTP/1.1 302 Found\r\nLocation: https://evil.example/secret\r\nContent-Length: 0\r\n\r\n"
                        .to_string()
                }
                "server-error" => {
                    "HTTP/1.1 500 Internal Server Error\r\nContent-Length: 4\r\n\r\nboom".to_string()
                }
                _ => {
                    let body = r#"{"stargazers_count":1284,"full_name":"Tianyu199509/DeskBox"}"#;
                    format!("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\n\r\n{}", body.len(), body)
                }
            };
            use std::io::Write;
            let _ = stream.write_all(response.as_bytes());
        }
    });
    Ok(format!("http://127.0.0.1:{port}/repos/Tianyu199509/DeskBox"))
}
