// Leg-3 spike guest (roadmap stage 3.5): the same GitHub-Stars behavior as
// the declarative and process legs, as WASM third-party code. The guest
// never touches the network or shell - network.fetch / shell.open are host
// imports gated host-side (requested AND in-scope AND granted), and every
// failure degrades to the fallback payload, same resilience contract.
//
// no_std on purpose: a pure component with zero WASI imports - the host
// embeds it synchronously without linking a WASI implementation, which is
// the minimal-plugin story the spike wants to prove.
#![no_std]

extern crate alloc;

use alloc::format;
use alloc::string::{String, ToString};

wit_bindgen::generate!({ world: "plugin-world", path: "../wit" });

const REPO_URL: &str = "https://api.github.com/repos/Tianyu199509/DeskBox";
const REPO_PAGE: &str = "https://github.com/Tianyu199509/DeskBox";
const FALLBACK: &str = "\u{2026}";

struct GuestImpl;

impl Guest for GuestImpl {
    fn activate(reason: String) {
        if reason == "spin" {
            // Governance demo: burn fuel until the host's limiter
            // interrupts the store (deterministic fuel, not a wall clock).
            loop {
                core::hint::black_box(0x42);
            }
        }

        match deskbox::plugin::capabilities::network_fetch(REPO_URL) {
            Ok(result) if result.status == 200 => {
                match extract_stars(&result.body) {
                    Some(stars) => deskbox::plugin::capabilities::widget_update(
                        "live-stars",
                        &stars.to_string(),
                        None,
                    ),
                    None => deskbox::plugin::capabilities::widget_update(
                        "live-stars",
                        FALLBACK,
                        Some("fallback (stargazers_count missing)"),
                    ),
                }
            }
            Ok(result) => deskbox::plugin::capabilities::widget_update(
                "live-stars",
                FALLBACK,
                Some(&format!("fallback (HTTP {})", result.status)),
            ),
            Err(refused) => deskbox::plugin::capabilities::widget_update(
                "live-stars",
                FALLBACK,
                Some(&format!("fallback ({refused})")),
            ),
        }
    }

    fn invoke_action(action_id: String) {
        if action_id == "open-repo" {
            // The host gates shell.open; a refusal is not fatal.
            let _ = deskbox::plugin::capabilities::shell_open(REPO_PAGE);
        }
    }
}

export!(GuestImpl);

// Spike-grade extraction: pull stargazers_count out of the JSON body
// without a parser (the product guest would use a real parser).
fn extract_stars(body: &str) -> Option<u64> {
    let key = "\"stargazers_count\":";
    let start = body.find(key)? + key.len();
    let rest = &body[start..];
    let digits: String = rest
        .chars()
        .skip_while(|c| c.is_whitespace())
        .take_while(|c| c.is_ascii_digit())
        .collect();
    if digits.is_empty() {
        return None;
    }
    digits.parse().ok()
}
