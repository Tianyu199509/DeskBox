//! Out-of-process Windows SMTC (`Windows.Media.Control`) session server.
//!
//! Runs in a dedicated `deskbox-thumbnail-proxy.exe --media-session-server`
//! child process so `Windows.Media.MediaControl.dll` is never loaded into the
//! main DeskBox UI process. All WinRT calls execute sequentially on a single
//! owner thread; WinRT event callbacks only post lightweight signals onto an
//! `mpsc` channel, eliminating cross-thread reentrancy and detach races.

use std::{
    collections::HashMap,
    io::{self, BufRead, Write},
    sync::mpsc,
    thread,
    time::{Duration, Instant},
};

use windows::{
    Foundation::TypedEventHandler,
    Media::{
        Control::{
            GlobalSystemMediaTransportControlsSession,
            GlobalSystemMediaTransportControlsSessionManager,
            GlobalSystemMediaTransportControlsSessionMediaProperties,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus,
        },
        MediaPlaybackAutoRepeatMode,
    },
    Storage::Streams::{
        DataReader, DataReaderLoadOperation, IRandomAccessStreamReference,
        IRandomAccessStreamWithContentType,
    },
    Win32::{
        Foundation::RPC_E_CHANGED_MODE,
        System::WinRT::{RO_INIT_MULTITHREADED, RoInitialize, RoUninitialize},
    },
    core::{IUnknown, Interface, Result as WinResult, RuntimeType},
};
use windows_future::{AsyncOperationCompletedHandler, AsyncStatus, IAsyncOperation};

const MANAGER_REQUEST_TIMEOUT: Duration = Duration::from_millis(5000);
const MEDIA_PROPERTIES_TIMEOUT: Duration = Duration::from_millis(2500);
const CONTROL_COMMAND_TIMEOUT: Duration = Duration::from_millis(2500);
const THUMBNAIL_READ_TIMEOUT: Duration = Duration::from_millis(2200);
const MAX_THUMBNAIL_BYTES: u64 = 8 * 1024 * 1024;
const DEFERRED_OBSERVED_PROMOTION_DELAY: Duration = Duration::from_millis(300);
const LOOP_IDLE_POLL: Duration = Duration::from_millis(250);

struct WinRtInitGuard {
    should_uninit: bool,
}

impl WinRtInitGuard {
    fn initialize_mta() -> Result<Self, String> {
        // SAFETY: Standard WinRT MTA initialization balanced by RoUninitialize.
        let hr = unsafe { RoInitialize(RO_INIT_MULTITHREADED) };
        match hr {
            Ok(()) => Ok(Self {
                should_uninit: true,
            }),
            Err(error) if error.code() == RPC_E_CHANGED_MODE => Ok(Self {
                should_uninit: false,
            }),
            Err(error) => Err(format!(
                "RoInitialize(MTA) failed: hr=0x{:08X} ({error})",
                error.code().0 as u32
            )),
        }
    }
}

impl Drop for WinRtInitGuard {
    fn drop(&mut self) {
        if self.should_uninit {
            // SAFETY: Balanced with a successful RoInitialize call on this thread.
            unsafe { RoUninitialize() };
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct CommandRequest {
    id: u64,
    op: String,
    preferred_session_id: Option<String>,
    session_id: Option<String>,
    known_cover_fingerprint: u64,
    action: Option<String>,
    position_ticks: i64,
    playback_mode: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum SignalKind {
    SessionsChanged,
    CurrentSessionChanged,
    SessionPlaybackInfoChanged,
    SessionMediaPropertiesChanged,
    SessionTimelinePropertiesChanged,
    ObservedPlaybackInfoChanged,
}

enum LoopMessage {
    Command(CommandRequest),
    Signal(SignalKind),
    Shutdown,
}

struct TrackedSession {
    session: GlobalSystemMediaTransportControlsSession,
    session_id: Option<String>,
    playback_token: Option<i64>,
    media_token: Option<i64>,
    timeline_token: Option<i64>,
}

struct ObservedSession {
    session: GlobalSystemMediaTransportControlsSession,
    playback_token: i64,
}

struct SessionEntry {
    session_id: String,
    session: GlobalSystemMediaTransportControlsSession,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum PlaybackState {
    Unknown,
    Stopped,
    Playing,
    Paused,
}

impl PlaybackState {
    fn as_str(self) -> &'static str {
        match self {
            Self::Unknown => "unknown",
            Self::Stopped => "stopped",
            Self::Playing => "playing",
            Self::Paused => "paused",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct SystemSessionCandidate {
    state: PlaybackState,
    is_closed: bool,
    is_system_current: bool,
    is_previous_current: bool,
}

struct MediaServerState {
    manager: GlobalSystemMediaTransportControlsSessionManager,
    sessions_token: Option<i64>,
    current_token: Option<i64>,
    current: Option<TrackedSession>,
    preferred_session_id: Option<String>,
    observed: Vec<ObservedSession>,
    signal_tx: mpsc::Sender<LoopMessage>,
    deferred_promotion_deadline: Option<Instant>,
}

impl MediaServerState {
    fn new(
        manager: GlobalSystemMediaTransportControlsSessionManager,
        signal_tx: mpsc::Sender<LoopMessage>,
    ) -> Self {
        let mut state = Self {
            manager,
            sessions_token: None,
            current_token: None,
            current: None,
            preferred_session_id: None,
            observed: Vec::new(),
            signal_tx,
            deferred_promotion_deadline: None,
        };
        state.subscribe_manager_events();
        state.sync_observed_sessions();
        let (initial_session, initial_id) = state.resolve_session(None);
        state.attach_current_session(initial_session, initial_id);
        state
    }

    fn subscribe_manager_events(&mut self) {
        let tx_sessions = self.signal_tx.clone();
        let sessions_handler = TypedEventHandler::new(move |_, _| {
            let _ = tx_sessions.send(LoopMessage::Signal(SignalKind::SessionsChanged));
            Ok(())
        });
        match self.manager.SessionsChanged(&sessions_handler) {
            Ok(token) => self.sessions_token = Some(token),
            Err(error) => log_winrt_error("subscribe SessionsChanged", &error),
        }

        let tx_current = self.signal_tx.clone();
        let current_handler = TypedEventHandler::new(move |_, _| {
            let _ = tx_current.send(LoopMessage::Signal(SignalKind::CurrentSessionChanged));
            Ok(())
        });
        match self.manager.CurrentSessionChanged(&current_handler) {
            Ok(token) => self.current_token = Some(token),
            Err(error) => log_winrt_error("subscribe CurrentSessionChanged", &error),
        }
    }

    fn teardown(&mut self) {
        self.detach_current_session();
        self.clear_observed_sessions();
        if let Some(token) = self.sessions_token.take() {
            let _ = self.manager.RemoveSessionsChanged(token);
        }
        if let Some(token) = self.current_token.take() {
            let _ = self.manager.RemoveCurrentSessionChanged(token);
        }
    }

    fn detach_current_session(&mut self) {
        if let Some(tracked) = self.current.take() {
            if let Some(token) = tracked.playback_token {
                let _ = tracked.session.RemovePlaybackInfoChanged(token);
            }
            if let Some(token) = tracked.media_token {
                let _ = tracked.session.RemoveMediaPropertiesChanged(token);
            }
            if let Some(token) = tracked.timeline_token {
                let _ = tracked.session.RemoveTimelinePropertiesChanged(token);
            }
        }
    }

    fn clear_observed_sessions(&mut self) {
        for observed in self.observed.drain(..) {
            let _ = observed
                .session
                .RemovePlaybackInfoChanged(observed.playback_token);
        }
    }

    fn sync_observed_sessions(&mut self) {
        let Ok(sessions) = self.manager.GetSessions() else {
            return;
        };

        let mut snapshot = Vec::new();
        if let Ok(count) = sessions.Size() {
            for index in 0..count {
                if let Ok(session) = sessions.GetAt(index) {
                    snapshot.push(session);
                }
            }
        }

        let mut i = self.observed.len();
        while i > 0 {
            i -= 1;
            let still_present = snapshot
                .iter()
                .any(|candidate| is_same_session(&self.observed[i].session, candidate));
            if !still_present || is_session_closed(&self.observed[i].session) {
                let removed = self.observed.swap_remove(i);
                let _ = removed
                    .session
                    .RemovePlaybackInfoChanged(removed.playback_token);
            }
        }

        for session in snapshot {
            if is_session_closed(&session)
                || self
                    .observed
                    .iter()
                    .any(|existing| is_same_session(&existing.session, &session))
            {
                continue;
            }

            let tx = self.signal_tx.clone();
            let handler = TypedEventHandler::new(move |_, _| {
                let _ = tx.send(LoopMessage::Signal(SignalKind::ObservedPlaybackInfoChanged));
                Ok(())
            });
            if let Ok(token) = session.PlaybackInfoChanged(&handler) {
                self.observed.push(ObservedSession {
                    session,
                    playback_token: token,
                });
            }
        }
    }

    fn enumerate_session_entries(&self) -> Vec<SessionEntry> {
        let Ok(sessions) = self.manager.GetSessions() else {
            return Vec::new();
        };
        let Ok(count) = sessions.Size() else {
            return Vec::new();
        };

        let mut ordinals: HashMap<String, usize> = HashMap::new();
        let mut entries = Vec::with_capacity(count as usize);
        for index in 0..count {
            let Ok(session) = sessions.GetAt(index) else {
                continue;
            };
            let source_app = session
                .SourceAppUserModelId()
                .map(|s| s.to_string_lossy())
                .unwrap_or_default();
            let key = source_app.to_ascii_lowercase();
            let ordinal = *ordinals.get(&key).unwrap_or(&0);
            ordinals.insert(key, ordinal + 1);
            let session_id = create_session_id(&source_app, ordinal);
            entries.push(SessionEntry {
                session_id,
                session,
            });
        }

        entries
    }

    fn resolve_session(
        &mut self,
        preferred_session_id: Option<&str>,
    ) -> (Option<GlobalSystemMediaTransportControlsSession>, Option<String>) {
        let preferred_clean = preferred_session_id
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .map(ToOwned::to_owned);
        self.preferred_session_id = preferred_clean.clone();

        let previous_session = self.current.as_ref().map(|t| t.session.clone());
        let previous_id = self.current.as_ref().and_then(|t| t.session_id.clone());

        let entries = self.enumerate_session_entries();
        if let Some(pref_id) = preferred_clean.as_deref() {
            if let Some(entry) = entries.iter().find(|entry| entry.session_id == pref_id) {
                if let Some(prev) = previous_session.as_ref()
                    && !is_session_closed(prev)
                    && is_same_session(prev, &entry.session)
                    && previous_id.as_deref() == Some(pref_id)
                {
                    return (Some(prev.clone()), previous_id);
                }

                if !is_session_closed(&entry.session) {
                    return (Some(entry.session.clone()), Some(entry.session_id.clone()));
                }
            }
            self.preferred_session_id = None;
        }

        let system_current = self.manager.GetCurrentSession().ok();
        if entries.is_empty() {
            if let (Some(prev), Some(sys)) = (previous_session.as_ref(), system_current.as_ref())
                && is_same_session(prev, sys)
            {
                return (Some(prev.clone()), previous_id);
            }
            return (system_current, None);
        }

        let system_current_listed = system_current
            .as_ref()
            .is_some_and(|sys| entries.iter().any(|entry| is_same_session(&entry.session, sys)));

        let mut candidate_entries: Vec<SessionEntry> = Vec::with_capacity(entries.len() + 1);
        let mut candidates: Vec<SystemSessionCandidate> = Vec::with_capacity(entries.len() + 1);

        if let Some(sys) = system_current.as_ref()
            && !system_current_listed
        {
            let source_app = sys
                .SourceAppUserModelId()
                .map(|s| s.to_string_lossy())
                .unwrap_or_default();
            let id = create_session_id(&source_app, 0);
            let is_prev = previous_session
                .as_ref()
                .is_some_and(|prev| is_same_session(prev, sys));
            candidates.push(build_candidate(sys, true, is_prev));
            candidate_entries.push(SessionEntry {
                session_id: id,
                session: sys.clone(),
            });
        }

        for entry in entries {
            let is_sys = system_current
                .as_ref()
                .is_some_and(|sys| is_same_session(&entry.session, sys));
            let is_prev = previous_session
                .as_ref()
                .is_some_and(|prev| is_same_session(prev, &entry.session));
            candidates.push(build_candidate(&entry.session, is_sys, is_prev));
            candidate_entries.push(entry);
        }

        let Some(selected_idx) = select_system_session_candidate_index(&candidates) else {
            return (None, None);
        };
        let selected = &candidate_entries[selected_idx];
        (Some(selected.session.clone()), Some(selected.session_id.clone()))
    }

    fn attach_current_session(
        &mut self,
        session: Option<GlobalSystemMediaTransportControlsSession>,
        session_id: Option<String>,
    ) {
        let Some(next_session) = session else {
            self.detach_current_session();
            return;
        };

        if let Some(existing) = self.current.as_mut()
            && is_same_session(&existing.session, &next_session)
        {
            if is_session_closed(&existing.session) {
                self.detach_current_session();
                return;
            }
            if session_id.as_deref().is_some_and(|id| !id.is_empty()) {
                existing.session_id = session_id;
            }
            return;
        }

        self.detach_current_session();

        let tx_playback = self.signal_tx.clone();
        let playback_token = next_session
            .PlaybackInfoChanged(&TypedEventHandler::new(move |_, _| {
                let _ = tx_playback.send(LoopMessage::Signal(
                    SignalKind::SessionPlaybackInfoChanged,
                ));
                Ok(())
            }))
            .ok();

        let tx_media = self.signal_tx.clone();
        let media_token = next_session
            .MediaPropertiesChanged(&TypedEventHandler::new(move |_, _| {
                let _ = tx_media.send(LoopMessage::Signal(
                    SignalKind::SessionMediaPropertiesChanged,
                ));
                Ok(())
            }))
            .ok();

        let tx_timeline = self.signal_tx.clone();
        let timeline_token = next_session
            .TimelinePropertiesChanged(&TypedEventHandler::new(move |_, _| {
                let _ = tx_timeline.send(LoopMessage::Signal(
                    SignalKind::SessionTimelinePropertiesChanged,
                ));
                Ok(())
            }))
            .ok();

        self.current = Some(TrackedSession {
            session: next_session,
            session_id,
            playback_token,
            media_token,
            timeline_token,
        });
    }

    fn handle_signals(&mut self, signals: &[SignalKind]) -> Vec<&'static str> {
        let mut has_sessions = false;
        let mut has_current = false;
        let mut has_playback = false;
        let mut has_media = false;
        let mut has_timeline = false;
        let mut has_observed = false;

        for signal in signals {
            match signal {
                SignalKind::SessionsChanged => has_sessions = true,
                SignalKind::CurrentSessionChanged => has_current = true,
                SignalKind::SessionPlaybackInfoChanged => has_playback = true,
                SignalKind::SessionMediaPropertiesChanged => has_media = true,
                SignalKind::SessionTimelinePropertiesChanged => has_timeline = true,
                SignalKind::ObservedPlaybackInfoChanged => has_observed = true,
            }
        }

        let mut emitted = Vec::new();
        let pref = self.preferred_session_id.clone();

        if has_sessions {
            self.sync_observed_sessions();
            let (resolved, resolved_id) = self.resolve_session(pref.as_deref());
            self.attach_current_session(resolved, resolved_id);
            emitted.push("sessions_changed");
        }

        if has_current {
            let (resolved, resolved_id) = self.resolve_session(pref.as_deref());
            self.attach_current_session(resolved, resolved_id);
            emitted.push("current_session_changed");
        }

        if has_observed {
            let previous = self.current.as_ref().map(|t| t.session.clone());
            let (resolved, resolved_id) = self.resolve_session(pref.as_deref());
            let changed = !option_same_session(previous.as_ref(), resolved.as_ref());
            if changed {
                self.attach_current_session(resolved, resolved_id);
                if !emitted.contains(&"current_session_changed") {
                    emitted.push("current_session_changed");
                }
            } else if pref.is_none() {
                self.deferred_promotion_deadline =
                    Some(Instant::now() + DEFERRED_OBSERVED_PROMOTION_DELAY);
            }
        }

        if has_playback {
            let current_closed = self
                .current
                .as_ref()
                .is_some_and(|t| is_session_closed(&t.session));
            if current_closed {
                self.detach_current_session();
                self.sync_observed_sessions();
                let (resolved, resolved_id) = self.resolve_session(pref.as_deref());
                self.attach_current_session(resolved, resolved_id);
                if !emitted.contains(&"current_session_changed") {
                    emitted.push("current_session_changed");
                }
            } else {
                emitted.push("playback_info_changed");
            }
        }

        if has_media {
            emitted.push("media_properties_changed");
        }

        if has_timeline {
            emitted.push("timeline_properties_changed");
        }

        emitted
    }

    fn run_deferred_promotion_check(&mut self) -> Option<&'static str> {
        self.deferred_promotion_deadline = None;
        if self.preferred_session_id.is_some() {
            return None;
        }

        let previous = self.current.as_ref().map(|t| t.session.clone());
        let (resolved, resolved_id) = self.resolve_session(None);
        if !option_same_session(previous.as_ref(), resolved.as_ref()) {
            self.attach_current_session(resolved, resolved_id);
            return Some("current_session_changed");
        }
        None
    }

    fn handle_command(&mut self, cmd: CommandRequest) -> String {
        match cmd.op.as_str() {
            "ping" => format!(r#"{{"type":"response","id":{},"ok":true}}"#, cmd.id),
            "get_sessions" => self.handle_get_sessions(cmd.id),
            "get_info" => self.handle_get_info(
                cmd.id,
                cmd.preferred_session_id.as_deref(),
                cmd.known_cover_fingerprint,
            ),
            "get_thumbnail" => self.handle_get_thumbnail(
                cmd.id,
                cmd.preferred_session_id.as_deref(),
                cmd.known_cover_fingerprint,
            ),
            "get_timeline" => {
                self.handle_get_timeline(cmd.id, cmd.preferred_session_id.as_deref())
            }
            "get_playback" => {
                self.handle_get_playback(cmd.id, cmd.preferred_session_id.as_deref())
            }
            "set_preferred" => {
                self.handle_set_preferred(cmd.id, cmd.session_id.as_deref().unwrap_or_default())
            }
            "control" => self.handle_control(&cmd),
            other => format!(
                r#"{{"type":"response","id":{},"ok":false,"error":{}}}"#,
                cmd.id,
                json_string(&format!("unknown op: {other}"))
            ),
        }
    }

    fn handle_get_sessions(&self, id: u64) -> String {
        let system_current = self.manager.GetCurrentSession().ok();
        let entries = self.enumerate_session_entries();
        let mut json = format!(r#"{{"type":"response","id":{id},"ok":true,"sessions":["#);
        let mut first = true;
        for entry in entries {
            let Ok(playback_info) = entry.session.GetPlaybackInfo() else {
                continue;
            };
            let source_app = entry
                .session
                .SourceAppUserModelId()
                .map(|s| s.to_string_lossy())
                .unwrap_or_default();
            let state = playback_info
                .PlaybackStatus()
                .map(map_playback_status)
                .unwrap_or(PlaybackState::Unknown);
            let is_sys = system_current
                .as_ref()
                .is_some_and(|sys| is_same_session(&entry.session, sys));
            if !first {
                json.push(',');
            }
            first = false;
            json.push_str(&format!(
                r#"{{"sessionId":{},"sourceAppUserModelId":{},"playbackState":{},"isSystemCurrent":{}}}"#,
                json_string(&entry.session_id),
                json_string(&source_app),
                json_string(state.as_str()),
                is_sys
            ));
        }
        json.push_str("]}");
        json
    }

    fn handle_get_info(
        &mut self,
        id: u64,
        preferred_session_id: Option<&str>,
        known_cover_fingerprint: u64,
    ) -> String {
        let (session, resolved_id) = self.resolve_session(preferred_session_id);
        self.attach_current_session(session.clone(), resolved_id.clone());
        let Some(session) = session else {
            return format!(r#"{{"type":"response","id":{id},"ok":true,"info":null}}"#);
        };

        let playback_info = match session.GetPlaybackInfo() {
            Ok(info) => info,
            Err(error) => {
                log_winrt_error("GetPlaybackInfo(get_info)", &error);
                self.invalidate_if_current(&session);
                return format!(r#"{{"type":"response","id":{id},"ok":true,"info":null}}"#);
            }
        };
        let timeline = match session.GetTimelineProperties() {
            Ok(tl) => tl,
            Err(error) => {
                log_winrt_error("GetTimelineProperties(get_info)", &error);
                self.invalidate_if_current(&session);
                return format!(r#"{{"type":"response","id":{id},"ok":true,"info":null}}"#);
            }
        };
        let controls = match playback_info.Controls() {
            Ok(c) => c,
            Err(error) => {
                log_winrt_error("PlaybackInfo.Controls(get_info)", &error);
                self.invalidate_if_current(&session);
                return format!(r#"{{"type":"response","id":{id},"ok":true,"info":null}}"#);
            }
        };

        let media_props = try_get_media_properties(&session);
        let title = media_props
            .as_ref()
            .and_then(|p| p.Title().ok())
            .map(|s| s.to_string_lossy())
            .unwrap_or_default();
        let artist = media_props
            .as_ref()
            .and_then(|p| p.Artist().ok())
            .map(|s| s.to_string_lossy())
            .unwrap_or_default();
        let album = media_props
            .as_ref()
            .and_then(|p| p.AlbumTitle().ok())
            .map(|s| s.to_string_lossy())
            .unwrap_or_default();

        let source_app = session
            .SourceAppUserModelId()
            .map(|s| s.to_string_lossy())
            .unwrap_or_default();
        let session_id = resolved_id
            .filter(|s| !s.is_empty())
            .unwrap_or_else(|| create_session_id(&source_app, 0));

        let (position_ticks, duration_ticks, updated_filetime_ticks) = extract_timeline(&timeline);
        let playback_state = playback_info
            .PlaybackStatus()
            .map(map_playback_status)
            .unwrap_or(PlaybackState::Unknown);
        let playback_rate = extract_playback_rate(&playback_info);
        let playback_mode = extract_playback_mode(&playback_info);

        let can_play = controls.IsPlayEnabled().unwrap_or(false);
        let can_pause = controls.IsPauseEnabled().unwrap_or(false);
        let can_prev = controls.IsPreviousEnabled().unwrap_or(false);
        let can_next = controls.IsNextEnabled().unwrap_or(false);
        let can_seek = controls.IsPlaybackPositionEnabled().unwrap_or(false);
        let can_shuffle = controls.IsShuffleEnabled().unwrap_or(false);
        let can_repeat = controls.IsRepeatEnabled().unwrap_or(false);

        let thumb = media_props
            .as_ref()
            .and_then(|p| p.Thumbnail().ok())
            .and_then(|thumb_ref| read_thumbnail_payload(&thumb_ref, known_cover_fingerprint));

        let mut thumb_fields = String::new();
        if let Some(payload) = thumb {
            thumb_fields.push_str(&format!(
                r#","hasThumbnail":true,"thumbnailFingerprint":"{}","thumbnailUnchanged":{}"#,
                payload.fingerprint, payload.unchanged
            ));
            if let Some(ct) = payload.content_type.as_deref() {
                thumb_fields.push_str(&format!(r#","thumbnailContentType":{}"#, json_string(ct)));
            }
            if let Some(b64) = payload.base64.as_deref() {
                thumb_fields.push_str(&format!(r#","thumbnailBase64":{}"#, json_string(b64)));
            }
        } else {
            thumb_fields.push_str(
                r#","hasThumbnail":false,"thumbnailFingerprint":"0","thumbnailUnchanged":false"#,
            );
        }

        format!(
            r#"{{"type":"response","id":{id},"ok":true,"info":{{"sessionId":{},"sourceAppUserModelId":{},"title":{},"artist":{},"album":{},"playbackState":{},"positionTicks":{},"durationTicks":{},"positionUpdatedFileTimeTicks":{},"playbackRate":{},"canPlay":{},"canPause":{},"canGoPrevious":{},"canGoNext":{},"canSeek":{},"canChangeShuffle":{},"canChangeRepeat":{},"playbackMode":{}{thumb_fields}}}}}"#,
            json_string(&session_id),
            json_string(&source_app),
            json_string(&title),
            json_string(&artist),
            json_string(&album),
            json_string(playback_state.as_str()),
            position_ticks,
            duration_ticks,
            updated_filetime_ticks,
            playback_rate,
            can_play,
            can_pause,
            can_prev,
            can_next,
            can_seek,
            can_shuffle,
            can_repeat,
            json_string(playback_mode),
        )
    }

    fn handle_get_thumbnail(
        &mut self,
        id: u64,
        preferred_session_id: Option<&str>,
        known_cover_fingerprint: u64,
    ) -> String {
        let (session, resolved_id) = self.resolve_session(preferred_session_id);
        self.attach_current_session(session.clone(), resolved_id);
        let Some(session) = session else {
            return format!(r#"{{"type":"response","id":{id},"ok":true,"thumbnail":null}}"#);
        };

        let Some(media_props) = try_get_media_properties(&session) else {
            return format!(r#"{{"type":"response","id":{id},"ok":true,"thumbnail":null}}"#);
        };
        let Ok(thumb_ref) = media_props.Thumbnail() else {
            return format!(r#"{{"type":"response","id":{id},"ok":true,"thumbnail":null}}"#);
        };
        let Some(payload) = read_thumbnail_payload(&thumb_ref, known_cover_fingerprint) else {
            return format!(r#"{{"type":"response","id":{id},"ok":true,"thumbnail":null}}"#);
        };

        let mut body = format!(
            r#"{{"fingerprint":"{}","unchanged":{}"#,
            payload.fingerprint, payload.unchanged
        );
        if let Some(ct) = payload.content_type.as_deref() {
            body.push_str(&format!(r#","contentType":{}"#, json_string(ct)));
        }
        if let Some(b64) = payload.base64.as_deref() {
            body.push_str(&format!(r#","base64":{}"#, json_string(b64)));
        }
        body.push('}');
        format!(r#"{{"type":"response","id":{id},"ok":true,"thumbnail":{body}}}"#)
    }

    fn handle_get_timeline(&mut self, id: u64, preferred_session_id: Option<&str>) -> String {
        let (session, resolved_id) = self.resolve_session(preferred_session_id);
        self.attach_current_session(session.clone(), resolved_id);
        let Some(session) = session else {
            return format!(r#"{{"type":"response","id":{id},"ok":true,"timeline":null}}"#);
        };

        let (Ok(timeline), Ok(playback_info)) =
            (session.GetTimelineProperties(), session.GetPlaybackInfo())
        else {
            self.invalidate_if_current(&session);
            return format!(r#"{{"type":"response","id":{id},"ok":true,"timeline":null}}"#);
        };

        let (position_ticks, duration_ticks, updated_filetime_ticks) = extract_timeline(&timeline);
        let playback_rate = extract_playback_rate(&playback_info);
        let playback_state = playback_info
            .PlaybackStatus()
            .map(map_playback_status)
            .unwrap_or(PlaybackState::Unknown);

        format!(
            r#"{{"type":"response","id":{id},"ok":true,"timeline":{{"positionTicks":{position_ticks},"durationTicks":{duration_ticks},"positionUpdatedFileTimeTicks":{updated_filetime_ticks},"playbackRate":{playback_rate},"playbackState":{}}}}}"#,
            json_string(playback_state.as_str())
        )
    }

    fn handle_get_playback(&mut self, id: u64, preferred_session_id: Option<&str>) -> String {
        let (session, resolved_id) = self.resolve_session(preferred_session_id);
        self.attach_current_session(session.clone(), resolved_id);
        let Some(session) = session else {
            return format!(r#"{{"type":"response","id":{id},"ok":true,"playback":null}}"#);
        };

        let Ok(playback_info) = session.GetPlaybackInfo() else {
            self.invalidate_if_current(&session);
            return format!(r#"{{"type":"response","id":{id},"ok":true,"playback":null}}"#);
        };
        let Ok(controls) = playback_info.Controls() else {
            self.invalidate_if_current(&session);
            return format!(r#"{{"type":"response","id":{id},"ok":true,"playback":null}}"#);
        };

        let state = playback_info
            .PlaybackStatus()
            .map(map_playback_status)
            .unwrap_or(PlaybackState::Unknown);
        let rate = extract_playback_rate(&playback_info);
        let mode = extract_playback_mode(&playback_info);

        format!(
            r#"{{"type":"response","id":{id},"ok":true,"playback":{{"playbackState":{},"playbackRate":{rate},"canPlay":{},"canPause":{},"canGoPrevious":{},"canGoNext":{},"canSeek":{},"canChangeShuffle":{},"canChangeRepeat":{},"playbackMode":{}}}}}"#,
            json_string(state.as_str()),
            controls.IsPlayEnabled().unwrap_or(false),
            controls.IsPauseEnabled().unwrap_or(false),
            controls.IsPreviousEnabled().unwrap_or(false),
            controls.IsNextEnabled().unwrap_or(false),
            controls.IsPlaybackPositionEnabled().unwrap_or(false),
            controls.IsShuffleEnabled().unwrap_or(false),
            controls.IsRepeatEnabled().unwrap_or(false),
            json_string(mode),
        )
    }

    fn handle_set_preferred(&mut self, id: u64, session_id: &str) -> String {
        let entries = self.enumerate_session_entries();
        if let Some(entry) = entries.into_iter().find(|e| e.session_id == session_id) {
            self.preferred_session_id = Some(entry.session_id.clone());
            self.attach_current_session(Some(entry.session), Some(entry.session_id));
            format!(r#"{{"type":"response","id":{id},"ok":true,"changed":true}}"#)
        } else {
            self.preferred_session_id = None;
            format!(r#"{{"type":"response","id":{id},"ok":true,"changed":false}}"#)
        }
    }

    fn handle_control(&mut self, cmd: &CommandRequest) -> String {
        let (session, resolved_id) = self.resolve_session(cmd.session_id.as_deref());
        self.attach_current_session(session.clone(), resolved_id);
        let Some(session) = session else {
            return format!(r#"{{"type":"response","id":{},"ok":true,"changed":false}}"#, cmd.id);
        };

        let action = cmd.action.as_deref().unwrap_or_default();
        let changed = match action {
            "play" => run_bool_control("TryPlayAsync", session.TryPlayAsync()),
            "pause" => run_bool_control("TryPauseAsync", session.TryPauseAsync()),
            "toggle_play_pause" => {
                run_bool_control("TryTogglePlayPauseAsync", session.TryTogglePlayPauseAsync())
            }
            "previous" => run_bool_control("TrySkipPreviousAsync", session.TrySkipPreviousAsync()),
            "next" => run_bool_control("TrySkipNextAsync", session.TrySkipNextAsync()),
            "seek" => run_bool_control(
                "TryChangePlaybackPositionAsync",
                session.TryChangePlaybackPositionAsync(cmd.position_ticks.max(0)),
            ),
            "playback_mode" => apply_playback_mode(
                &session,
                cmd.playback_mode.as_deref().unwrap_or("normal"),
            ),
            _ => false,
        };

        format!(
            r#"{{"type":"response","id":{},"ok":true,"changed":{changed}}}"#,
            cmd.id
        )
    }

    fn invalidate_if_current(&mut self, session: &GlobalSystemMediaTransportControlsSession) {
        if self
            .current
            .as_ref()
            .is_some_and(|tracked| is_same_session(&tracked.session, session))
        {
            self.detach_current_session();
        }
    }
}

struct ThumbnailPayload {
    fingerprint: u64,
    unchanged: bool,
    content_type: Option<String>,
    base64: Option<String>,
}

pub(crate) fn run_media_session_server() -> Result<i32, String> {
    let _winrt_guard = WinRtInitGuard::initialize_mta()?;

    let request_op = GlobalSystemMediaTransportControlsSessionManager::RequestAsync()
        .map_err(|error| {
            format!(
                "SMTC RequestAsync initiation failed: hr=0x{:08X} ({error})",
                error.code().0 as u32
            )
        })?;
    let manager = await_async_op(request_op, MANAGER_REQUEST_TIMEOUT, "RequestAsync")?;

    let (tx, rx) = mpsc::channel::<LoopMessage>();
    let mut state = MediaServerState::new(manager, tx.clone());

    write_line(r#"{"type":"ready"}"#)?;

    let stdin_tx = tx.clone();
    thread::Builder::new()
        .name("deskbox-media-stdin".to_string())
        .spawn(move || {
            let stdin = io::stdin();
            let mut reader = stdin.lock();
            let mut line = String::new();
            loop {
                line.clear();
                match reader.read_line(&mut line) {
                    Ok(0) => {
                        let _ = stdin_tx.send(LoopMessage::Shutdown);
                        break;
                    }
                    Ok(_) => {
                        let trimmed = line.trim().trim_start_matches('\u{feff}');
                        if trimmed.is_empty() {
                            continue;
                        }
                        if let Some(cmd) = parse_command_json(trimmed) {
                            let is_shutdown = cmd.op == "shutdown";
                            if is_shutdown {
                                let _ = stdin_tx.send(LoopMessage::Command(cmd));
                                let _ = stdin_tx.send(LoopMessage::Shutdown);
                                break;
                            }
                            if stdin_tx.send(LoopMessage::Command(cmd)).is_err() {
                                break;
                            }
                        } else {
                            eprintln!("[media-session-server] ignored malformed command line");
                        }
                    }
                    Err(error) => {
                        eprintln!("[media-session-server] stdin read failed: {error}");
                        let _ = stdin_tx.send(LoopMessage::Shutdown);
                        break;
                    }
                }
            }
        })
        .map_err(|error| format!("failed to spawn media stdin reader: {error}"))?;

    let mut pending_signals: Vec<SignalKind> = Vec::new();
    let mut shutting_down = false;

    while !shutting_down {
        let wait_duration = match state.deferred_promotion_deadline {
            Some(deadline) => deadline
                .saturating_duration_since(Instant::now())
                .min(LOOP_IDLE_POLL),
            None => LOOP_IDLE_POLL,
        };

        match rx.recv_timeout(wait_duration) {
            Ok(first_msg) => {
                process_message(first_msg, &mut state, &mut pending_signals, &mut shutting_down)?;
                // Drain bursty messages so rapid WinRT callbacks coalesce cleanly.
                while !shutting_down {
                    match rx.try_recv() {
                        Ok(next_msg) => {
                            process_message(
                                next_msg,
                                &mut state,
                                &mut pending_signals,
                                &mut shutting_down,
                            )?;
                        }
                        Err(mpsc::TryRecvError::Empty) => break,
                        Err(mpsc::TryRecvError::Disconnected) => {
                            shutting_down = true;
                            break;
                        }
                    }
                }
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {}
            Err(mpsc::RecvTimeoutError::Disconnected) => {
                shutting_down = true;
            }
        }

        if !shutting_down && !pending_signals.is_empty() {
            let events = state.handle_signals(&pending_signals);
            pending_signals.clear();
            for event_name in events {
                write_line(&format!(r#"{{"type":"event","name":"{event_name}"}}"#))?;
            }
        }

        if !shutting_down
            && let Some(deadline) = state.deferred_promotion_deadline
            && Instant::now() >= deadline
            && let Some(event_name) = state.run_deferred_promotion_check()
        {
            write_line(&format!(r#"{{"type":"event","name":"{event_name}"}}"#))?;
        }
    }

    state.teardown();
    let _ = write_line(r#"{"type":"bye"}"#);
    Ok(0)
}

fn process_message(
    msg: LoopMessage,
    state: &mut MediaServerState,
    pending_signals: &mut Vec<SignalKind>,
    shutting_down: &mut bool,
) -> Result<(), String> {
    match msg {
        LoopMessage::Shutdown => {
            *shutting_down = true;
        }
        LoopMessage::Signal(signal) => {
            if !pending_signals.contains(&signal) {
                pending_signals.push(signal);
            }
        }
        LoopMessage::Command(cmd) => {
            if cmd.op == "shutdown" {
                write_line(&format!(r#"{{"type":"response","id":{},"ok":true}}"#, cmd.id))?;
                *shutting_down = true;
            } else {
                let response = state.handle_command(cmd);
                write_line(&response)?;
            }
        }
    }
    Ok(())
}

fn write_line(line: &str) -> Result<(), String> {
    let mut stdout = io::stdout().lock();
    stdout
        .write_all(line.as_bytes())
        .and_then(|_| stdout.write_all(b"\n"))
        .and_then(|_| stdout.flush())
        .map_err(|error| format!("stdout write failed: {error}"))
}

fn await_async_op<T: RuntimeType>(
    op: IAsyncOperation<T>,
    timeout: Duration,
    label: &str,
) -> Result<T, String> {
    if op.Status().ok() != Some(AsyncStatus::Started) {
        return op.GetResults().map_err(|error| {
            format!(
                "{label} failed: hr=0x{:08X} ({error})",
                error.code().0 as u32
            )
        });
    }

    let (tx, rx) = mpsc::sync_channel::<()>(1);
    let handler = AsyncOperationCompletedHandler::new(move |_, _| {
        let _ = tx.try_send(());
        Ok(())
    });
    op.SetCompleted(&handler).map_err(|error| {
        format!(
            "{label} SetCompleted failed: hr=0x{:08X} ({error})",
            error.code().0 as u32
        )
    })?;

    match rx.recv_timeout(timeout) {
        Ok(()) => op.GetResults().map_err(|error| {
            format!(
                "{label} GetResults failed: hr=0x{:08X} ({error})",
                error.code().0 as u32
            )
        }),
        Err(_) => {
            let _ = op.Cancel();
            Err(format!(
                "{label} timed out after {}ms",
                timeout.as_millis()
            ))
        }
    }
}

fn await_data_reader_load(
    op: DataReaderLoadOperation,
    timeout: Duration,
) -> Result<u32, String> {
    let async_op: IAsyncOperation<u32> = op.cast().map_err(|error| {
        format!(
            "DataReaderLoadOperation cast failed: hr=0x{:08X} ({error})",
            error.code().0 as u32
        )
    })?;
    await_async_op(async_op, timeout, "DataReader.LoadAsync")
}

fn try_get_media_properties(
    session: &GlobalSystemMediaTransportControlsSession,
) -> Option<GlobalSystemMediaTransportControlsSessionMediaProperties> {
    let op = match session.TryGetMediaPropertiesAsync() {
        Ok(op) => op,
        Err(error) => {
            log_winrt_error("TryGetMediaPropertiesAsync(initiate)", &error);
            return None;
        }
    };

    match await_async_op(op, MEDIA_PROPERTIES_TIMEOUT, "TryGetMediaPropertiesAsync") {
        Ok(props) => Some(props),
        Err(message) => {
            eprintln!("[media-session-server] {message}");
            None
        }
    }
}

fn read_thumbnail_payload(
    thumb_ref: &IRandomAccessStreamReference,
    known_fingerprint: u64,
) -> Option<ThumbnailPayload> {
    let deadline = Instant::now() + THUMBNAIL_READ_TIMEOUT;
    let open_op = thumb_ref.OpenReadAsync().ok()?;
    let open_budget = deadline.checked_duration_since(Instant::now())?;
    let stream: IRandomAccessStreamWithContentType =
        await_async_op(open_op, open_budget, "Thumbnail.OpenReadAsync")
            .map_err(|msg| eprintln!("[media-session-server] {msg}"))
            .ok()?;

    let raw_size = stream.Size().ok()?;
    if raw_size > MAX_THUMBNAIL_BYTES {
        return None;
    }

    let content_type = stream
        .ContentType()
        .ok()
        .map(|s| s.to_string_lossy())
        .filter(|s| !s.is_empty());

    let input = stream.GetInputStreamAt(0).ok()?;
    let reader = DataReader::CreateDataReader(&input).ok()?;
    let mut bytes = Vec::with_capacity(if raw_size > 0 { raw_size as usize } else { 16 * 1024 });
    let target_size = if raw_size > 0 { raw_size as usize } else { MAX_THUMBNAIL_BYTES as usize };
    const CHUNK_SIZE: u32 = 64 * 1024;

    while bytes.len() < target_size {
        let remaining = (target_size - bytes.len()).min(CHUNK_SIZE as usize) as u32;
        if remaining == 0 {
            break;
        }

        let Some(chunk_budget) = deadline.checked_duration_since(Instant::now()) else {
            eprintln!("[media-session-server] Thumbnail read exceeded total timeout budget");
            let _ = reader.DetachStream();
            return None;
        };

        let load_op = match reader.LoadAsync(remaining) {
            Ok(op) => op,
            Err(_) => {
                let _ = reader.DetachStream();
                return None;
            }
        };
        let loaded = match await_data_reader_load(load_op, chunk_budget) {
            Ok(n) => n as usize,
            Err(msg) => {
                eprintln!("[media-session-server] {msg}");
                let _ = reader.DetachStream();
                return None;
            }
        };
        if loaded == 0 {
            break;
        }

        let start = bytes.len();
        bytes.resize(start + loaded, 0);
        if reader.ReadBytes(&mut bytes[start..start + loaded]).is_err() {
            let _ = reader.DetachStream();
            return None;
        }
    }
    let _ = reader.DetachStream();

    if bytes.is_empty() || (raw_size > 0 && bytes.len() < raw_size as usize) {
        return None;
    }

    let fingerprint = compute_thumbnail_fingerprint(&bytes);
    if fingerprint == 0 {
        return None;
    }

    if known_fingerprint != 0 && known_fingerprint == fingerprint {
        return Some(ThumbnailPayload {
            fingerprint,
            unchanged: true,
            content_type,
            base64: None,
        });
    }

    Some(ThumbnailPayload {
        fingerprint,
        unchanged: false,
        content_type,
        base64: Some(encode_base64(&bytes)),
    })
}

fn run_bool_control(label: &str, initiated: WinResult<IAsyncOperation<bool>>) -> bool {
    let op = match initiated {
        Ok(op) => op,
        Err(error) => {
            log_winrt_error(label, &error);
            return false;
        }
    };
    match await_async_op(op, CONTROL_COMMAND_TIMEOUT, label) {
        Ok(value) => value,
        Err(message) => {
            eprintln!("[media-session-server] {message}");
            false
        }
    }
}

fn apply_playback_mode(session: &GlobalSystemMediaTransportControlsSession, mode: &str) -> bool {
    let Ok(playback_info) = session.GetPlaybackInfo() else {
        return false;
    };
    let Ok(controls) = playback_info.Controls() else {
        return false;
    };

    let is_shuffle_enabled = controls.IsShuffleEnabled().unwrap_or(false);
    let is_repeat_enabled = controls.IsRepeatEnabled().unwrap_or(false);
    let is_shuffle_active = playback_info
        .IsShuffleActive()
        .ok()
        .and_then(|r| r.Value().ok())
        .unwrap_or(false);
    let repeat_mode = playback_info
        .AutoRepeatMode()
        .ok()
        .and_then(|r| r.Value().ok())
        .unwrap_or(MediaPlaybackAutoRepeatMode::None);

    let mut changed = false;
    match mode {
        "shuffle" => {
            if !is_shuffle_enabled {
                return false;
            }
            if is_repeat_enabled && repeat_mode != MediaPlaybackAutoRepeatMode::None {
                changed |= run_bool_control(
                    "TryChangeAutoRepeatModeAsync",
                    session.TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode::None),
                );
            }
            changed |= run_bool_control(
                "TryChangeShuffleActiveAsync",
                session.TryChangeShuffleActiveAsync(true),
            );
            changed
        }
        "repeat" => {
            if !is_repeat_enabled {
                return false;
            }
            if is_shuffle_enabled && is_shuffle_active {
                changed |= run_bool_control(
                    "TryChangeShuffleActiveAsync",
                    session.TryChangeShuffleActiveAsync(false),
                );
            }
            changed |= run_bool_control(
                "TryChangeAutoRepeatModeAsync",
                session.TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode::List),
            );
            changed
        }
        "repeat_one" => {
            if !is_repeat_enabled {
                return false;
            }
            if is_shuffle_enabled && is_shuffle_active {
                changed |= run_bool_control(
                    "TryChangeShuffleActiveAsync",
                    session.TryChangeShuffleActiveAsync(false),
                );
            }
            changed |= run_bool_control(
                "TryChangeAutoRepeatModeAsync",
                session.TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode::Track),
            );
            changed
        }
        _ => {
            if is_shuffle_enabled && is_shuffle_active {
                changed |= run_bool_control(
                    "TryChangeShuffleActiveAsync",
                    session.TryChangeShuffleActiveAsync(false),
                );
            }
            if is_repeat_enabled && repeat_mode != MediaPlaybackAutoRepeatMode::None {
                changed |= run_bool_control(
                    "TryChangeAutoRepeatModeAsync",
                    session.TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode::None),
                );
            }
            changed
        }
    }
}

fn extract_timeline(
    timeline: &windows::Media::Control::GlobalSystemMediaTransportControlsSessionTimelineProperties,
) -> (i64, i64, i64) {
    let start = timeline.StartTime().map(|t| t.Duration).unwrap_or(0);
    let end = timeline.EndTime().map(|t| t.Duration).unwrap_or(0);
    let pos = timeline.Position().map(|t| t.Duration).unwrap_or(0);
    let raw_pos = if start > 0 && pos >= start {
        pos - start
    } else {
        pos
    };
    let position_ticks = raw_pos.max(0);
    let duration_ticks = if end > start { end - start } else { 0 };
    let updated_filetime_ticks = timeline
        .LastUpdatedTime()
        .map(|dt| dt.UniversalTime)
        .unwrap_or(0);
    (position_ticks, duration_ticks, updated_filetime_ticks)
}

fn extract_playback_rate(
    playback_info: &windows::Media::Control::GlobalSystemMediaTransportControlsSessionPlaybackInfo,
) -> f64 {
    playback_info
        .PlaybackRate()
        .ok()
        .and_then(|r| r.Value().ok())
        .filter(|&rate| rate.is_finite() && (0.05..=4.0).contains(&rate))
        .unwrap_or(1.0)
}

fn extract_playback_mode(
    playback_info: &windows::Media::Control::GlobalSystemMediaTransportControlsSessionPlaybackInfo,
) -> &'static str {
    let shuffle = playback_info
        .IsShuffleActive()
        .ok()
        .and_then(|r| r.Value().ok())
        .unwrap_or(false);
    if shuffle {
        return "shuffle";
    }

    match playback_info
        .AutoRepeatMode()
        .ok()
        .and_then(|r| r.Value().ok())
    {
        Some(MediaPlaybackAutoRepeatMode::Track) => "repeat_one",
        Some(MediaPlaybackAutoRepeatMode::List) => "repeat",
        _ => "normal",
    }
}

fn map_playback_status(
    status: GlobalSystemMediaTransportControlsSessionPlaybackStatus,
) -> PlaybackState {
    match status {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus::Playing => PlaybackState::Playing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus::Paused => PlaybackState::Paused,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus::Stopped => PlaybackState::Stopped,
        _ => PlaybackState::Unknown,
    }
}

fn is_session_closed(session: &GlobalSystemMediaTransportControlsSession) -> bool {
    match session.GetPlaybackInfo().and_then(|i| i.PlaybackStatus()) {
        Ok(status) => status == GlobalSystemMediaTransportControlsSessionPlaybackStatus::Closed,
        Err(_) => true,
    }
}

fn build_candidate(
    session: &GlobalSystemMediaTransportControlsSession,
    is_system_current: bool,
    is_previous_current: bool,
) -> SystemSessionCandidate {
    match session.GetPlaybackInfo().and_then(|i| i.PlaybackStatus()) {
        Ok(status) => SystemSessionCandidate {
            state: map_playback_status(status),
            is_closed: status == GlobalSystemMediaTransportControlsSessionPlaybackStatus::Closed,
            is_system_current,
            is_previous_current,
        },
        Err(_) => SystemSessionCandidate {
            state: PlaybackState::Unknown,
            is_closed: true,
            is_system_current,
            is_previous_current,
        },
    }
}

fn select_system_session_candidate_index(candidates: &[SystemSessionCandidate]) -> Option<usize> {
    if candidates.is_empty() {
        return None;
    }

    if let Some(idx) = candidates
        .iter()
        .position(|c| c.state == PlaybackState::Playing && c.is_system_current)
    {
        return Some(idx);
    }
    if let Some(idx) = candidates
        .iter()
        .position(|c| c.state == PlaybackState::Playing)
    {
        return Some(idx);
    }
    if let Some(idx) = candidates
        .iter()
        .position(|c| c.is_previous_current && !c.is_closed && c.state == PlaybackState::Unknown)
    {
        return Some(idx);
    }
    if let Some(idx) = candidates
        .iter()
        .position(|c| c.is_system_current && !c.is_closed)
    {
        return Some(idx);
    }
    if let Some(idx) = candidates
        .iter()
        .position(|c| c.is_previous_current && !c.is_closed)
    {
        return Some(idx);
    }
    if let Some(idx) = candidates
        .iter()
        .position(|c| c.state == PlaybackState::Paused)
    {
        return Some(idx);
    }
    if let Some(idx) = candidates.iter().position(|c| !c.is_closed) {
        return Some(idx);
    }

    Some(0)
}

fn is_same_session(
    left: &GlobalSystemMediaTransportControlsSession,
    right: &GlobalSystemMediaTransportControlsSession,
) -> bool {
    if left == right {
        return true;
    }
    match (left.cast::<IUnknown>(), right.cast::<IUnknown>()) {
        (Ok(lu), Ok(ru)) => lu.as_raw() == ru.as_raw(),
        _ => false,
    }
}

fn option_same_session(
    left: Option<&GlobalSystemMediaTransportControlsSession>,
    right: Option<&GlobalSystemMediaTransportControlsSession>,
) -> bool {
    match (left, right) {
        (None, None) => true,
        (Some(l), Some(r)) => is_same_session(l, r),
        _ => false,
    }
}

fn create_session_id(source_app_user_model_id: &str, ordinal: usize) -> String {
    format!("{source_app_user_model_id}\u{001F}{ordinal}")
}

fn log_winrt_error(context: &str, error: &windows::core::Error) {
    eprintln!(
        "[media-session-server] {context} failed: hr=0x{:08X} ({error})",
        error.code().0 as u32
    );
}

/// Exact FNV-1a 64-bit fingerprint matching `MusicWidgetViewModel.ComputeThumbnailFingerprint`.
pub(crate) fn compute_thumbnail_fingerprint(bytes: &[u8]) -> u64 {
    if bytes.is_empty() {
        return 0;
    }

    const FNV_OFFSET: u64 = 14_695_981_039_346_656_037;
    const FNV_PRIME: u64 = 1_099_511_628_211;
    let mut hash = (FNV_OFFSET ^ (bytes.len() as u64)).wrapping_mul(FNV_PRIME);

    let mut offset = 0;
    while offset + 8 <= bytes.len() {
        let word = u64::from_le_bytes([
            bytes[offset],
            bytes[offset + 1],
            bytes[offset + 2],
            bytes[offset + 3],
            bytes[offset + 4],
            bytes[offset + 5],
            bytes[offset + 6],
            bytes[offset + 7],
        ]);
        hash = (hash ^ word).wrapping_mul(FNV_PRIME);
        offset += 8;
    }

    while offset < bytes.len() {
        hash = (hash ^ (bytes[offset] as u64)).wrapping_mul(FNV_PRIME);
        offset += 1;
    }

    if hash == 0 { 1 } else { hash }
}

fn encode_base64(input: &[u8]) -> String {
    const TABLE: &[u8; 64] =
        b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::with_capacity(input.len().div_ceil(3) * 4);
    let mut i = 0;
    while i + 3 <= input.len() {
        let n = ((input[i] as u32) << 16) | ((input[i + 1] as u32) << 8) | (input[i + 2] as u32);
        out.push(TABLE[((n >> 18) & 0x3F) as usize] as char);
        out.push(TABLE[((n >> 12) & 0x3F) as usize] as char);
        out.push(TABLE[((n >> 6) & 0x3F) as usize] as char);
        out.push(TABLE[(n & 0x3F) as usize] as char);
        i += 3;
    }
    match input.len() - i {
        1 => {
            let n = (input[i] as u32) << 16;
            out.push(TABLE[((n >> 18) & 0x3F) as usize] as char);
            out.push(TABLE[((n >> 12) & 0x3F) as usize] as char);
            out.push('=');
            out.push('=');
        }
        2 => {
            let n = ((input[i] as u32) << 16) | ((input[i + 1] as u32) << 8);
            out.push(TABLE[((n >> 18) & 0x3F) as usize] as char);
            out.push(TABLE[((n >> 12) & 0x3F) as usize] as char);
            out.push(TABLE[((n >> 6) & 0x3F) as usize] as char);
            out.push('=');
        }
        _ => {}
    }
    out
}

fn json_string(value: &str) -> String {
    let mut out = String::with_capacity(value.len() + 2);
    out.push('"');
    for ch in value.chars() {
        match ch {
            '"' => out.push_str(r#"\""#),
            '\\' => out.push_str(r#"\\"#),
            '\n' => out.push_str(r#"\n"#),
            '\r' => out.push_str(r#"\r"#),
            '\t' => out.push_str(r#"\t"#),
            '\u{08}' => out.push_str(r#"\b"#),
            '\u{0C}' => out.push_str(r#"\f"#),
            c if (c as u32) < 0x20 => {
                out.push_str(&format!(r#"\u{:04x}"#, c as u32));
            }
            c => out.push(c),
        }
    }
    out.push('"');
    out
}

/// Minimal flat-JSON object parser for commands sent by `MediaSessionProxyClient`.
/// Avoids pulling in `serde`/`serde_json` to keep the native proxy binary small.
fn parse_command_json(line: &str) -> Option<CommandRequest> {
    let bytes = line.as_bytes();
    let mut pos = 0;
    skip_ws(bytes, &mut pos);
    if bytes.get(pos) != Some(&b'{') {
        return None;
    }
    pos += 1;

    let mut id: Option<u64> = None;
    let mut op: Option<String> = None;
    let mut preferred_session_id: Option<String> = None;
    let mut session_id: Option<String> = None;
    let mut known_cover_fingerprint: u64 = 0;
    let mut action: Option<String> = None;
    let mut position_ticks: i64 = 0;
    let mut playback_mode: Option<String> = None;

    loop {
        skip_ws(bytes, &mut pos);
        if bytes.get(pos) == Some(&b'}') {
            break;
        }
        let key = parse_json_string(bytes, &mut pos)?;
        skip_ws(bytes, &mut pos);
        if bytes.get(pos) != Some(&b':') {
            return None;
        }
        pos += 1;
        skip_ws(bytes, &mut pos);

        match key.as_str() {
            "id" => id = parse_json_u64(bytes, &mut pos),
            "op" => op = parse_json_nullable_string(bytes, &mut pos)?,
            "preferredSessionId" => {
                preferred_session_id = parse_json_nullable_string(bytes, &mut pos)?
            }
            "sessionId" => session_id = parse_json_nullable_string(bytes, &mut pos)?,
            "knownCoverFingerprint" => {
                if bytes.get(pos) == Some(&b'"') {
                    let s = parse_json_string(bytes, &mut pos)?;
                    known_cover_fingerprint = s.parse::<u64>().unwrap_or(0);
                } else if let Some(n) = parse_json_u64(bytes, &mut pos) {
                    known_cover_fingerprint = n;
                } else {
                    skip_json_value(bytes, &mut pos)?;
                }
            }
            "action" => action = parse_json_nullable_string(bytes, &mut pos)?,
            "positionTicks" => position_ticks = parse_json_i64(bytes, &mut pos)?,
            "playbackMode" => playback_mode = parse_json_nullable_string(bytes, &mut pos)?,
            _ => skip_json_value(bytes, &mut pos)?,
        }

        skip_ws(bytes, &mut pos);
        match bytes.get(pos) {
            Some(&b',') => pos += 1,
            Some(&b'}') => break,
            _ => return None,
        }
    }

    Some(CommandRequest {
        id: id?,
        op: op?,
        preferred_session_id,
        session_id,
        known_cover_fingerprint,
        action,
        position_ticks,
        playback_mode,
    })
}

fn skip_ws(bytes: &[u8], pos: &mut usize) {
    while let Some(&b) = bytes.get(*pos) {
        if matches!(b, b' ' | b'\t' | b'\r' | b'\n') {
            *pos += 1;
        } else {
            break;
        }
    }
}

fn parse_json_nullable_string(bytes: &[u8], pos: &mut usize) -> Option<Option<String>> {
    if bytes.get(*pos..*pos + 4) == Some(b"null") {
        *pos += 4;
        return Some(None);
    }
    parse_json_string(bytes, pos).map(Some)
}

fn parse_json_string(bytes: &[u8], pos: &mut usize) -> Option<String> {
    if bytes.get(*pos) != Some(&b'"') {
        return None;
    }
    *pos += 1;
    let mut out = String::new();
    while let Some(&b) = bytes.get(*pos) {
        if b == b'"' {
            *pos += 1;
            return Some(out);
        }
        if b == b'\\' {
            *pos += 1;
            let esc = *bytes.get(*pos)?;
            *pos += 1;
            match esc {
                b'"' => out.push('"'),
                b'\\' => out.push('\\'),
                b'/' => out.push('/'),
                b'b' => out.push('\u{08}'),
                b'f' => out.push('\u{0C}'),
                b'n' => out.push('\n'),
                b'r' => out.push('\r'),
                b't' => out.push('\t'),
                b'u' => {
                    let code_unit = parse_hex4(bytes, pos)?;
                    if (0xD800..=0xDBFF).contains(&code_unit)
                        && bytes.get(*pos..*pos + 2) == Some(b"\\u")
                    {
                        *pos += 2;
                        let low = parse_hex4(bytes, pos)?;
                        if (0xDC00..=0xDFFF).contains(&low) {
                            let combined = 0x10000
                                + (((code_unit as u32 - 0xD800) << 10) | (low as u32 - 0xDC00));
                            if let Some(ch) = char::from_u32(combined) {
                                out.push(ch);
                                continue;
                            }
                        }
                        return None;
                    }
                    out.push(char::from_u32(code_unit as u32)?);
                }
                _ => return None,
            }
        } else {
            let start = *pos;
            while let Some(&next) = bytes.get(*pos) {
                if next == b'"' || next == b'\\' {
                    break;
                }
                *pos += 1;
            }
            let slice = std::str::from_utf8(&bytes[start..*pos]).ok()?;
            out.push_str(slice);
        }
    }
    None
}

fn parse_hex4(bytes: &[u8], pos: &mut usize) -> Option<u16> {
    let slice = bytes.get(*pos..*pos + 4)?;
    let text = std::str::from_utf8(slice).ok()?;
    let value = u16::from_str_radix(text, 16).ok()?;
    *pos += 4;
    Some(value)
}

fn parse_json_u64(bytes: &[u8], pos: &mut usize) -> Option<u64> {
    let start = *pos;
    while let Some(&b) = bytes.get(*pos) {
        if b.is_ascii_digit() {
            *pos += 1;
        } else {
            break;
        }
    }
    if *pos == start {
        return None;
    }
    std::str::from_utf8(&bytes[start..*pos]).ok()?.parse().ok()
}

fn parse_json_i64(bytes: &[u8], pos: &mut usize) -> Option<i64> {
    let start = *pos;
    if bytes.get(*pos) == Some(&b'-') {
        *pos += 1;
    }
    let digits_start = *pos;
    while let Some(&b) = bytes.get(*pos) {
        if b.is_ascii_digit() {
            *pos += 1;
        } else {
            break;
        }
    }
    if *pos == digits_start {
        return None;
    }
    std::str::from_utf8(&bytes[start..*pos]).ok()?.parse().ok()
}

fn skip_json_value(bytes: &[u8], pos: &mut usize) -> Option<()> {
    skip_ws(bytes, pos);
    match *bytes.get(*pos)? {
        b'"' => {
            let _ = parse_json_string(bytes, pos)?;
            Some(())
        }
        b'{' | b'[' => {
            let mut depth = 0usize;
            while let Some(&b) = bytes.get(*pos) {
                match b {
                    b'"' => {
                        let _ = parse_json_string(bytes, pos)?;
                    }
                    b'{' | b'[' => {
                        depth += 1;
                        *pos += 1;
                    }
                    b'}' | b']' => {
                        depth = depth.saturating_sub(1);
                        *pos += 1;
                        if depth == 0 {
                            return Some(());
                        }
                    }
                    _ => *pos += 1,
                }
            }
            None
        }
        _ => {
            let start = *pos;
            while let Some(&b) = bytes.get(*pos) {
                if matches!(b, b',' | b'}' | b']' | b' ' | b'\t' | b'\r' | b'\n') {
                    break;
                }
                *pos += 1;
            }
            (*pos > start).then_some(())
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn command_parser_handles_unicode_escapes_and_numbers() {
        let line = r#"{"id":42,"op":"get_info","preferredSessionId":"AppleMusic.exe\u001f0","knownCoverFingerprint":"12345678901234567890"}"#;
        let cmd = parse_command_json(line).expect("parsed command");
        assert_eq!(cmd.id, 42);
        assert_eq!(cmd.op, "get_info");
        assert_eq!(
            cmd.preferred_session_id.as_deref(),
            Some("AppleMusic.exe\u{001F}0")
        );
        assert_eq!(cmd.known_cover_fingerprint, 12345678901234567890);
    }

    #[test]
    fn command_parser_handles_seek_control_and_null_fields() {
        let line = r#"{"id":7,"op":"control","sessionId":null,"action":"seek","positionTicks":1250000000}"#;
        let cmd = parse_command_json(line).expect("parsed control");
        assert_eq!(cmd.id, 7);
        assert_eq!(cmd.op, "control");
        assert!(cmd.session_id.is_none());
        assert_eq!(cmd.action.as_deref(), Some("seek"));
        assert_eq!(cmd.position_ticks, 1_250_000_000);
    }

    #[test]
    fn base64_encoder_matches_rfc4648_vectors() {
        assert_eq!(encode_base64(b""), "");
        assert_eq!(encode_base64(b"f"), "Zg==");
        assert_eq!(encode_base64(b"fo"), "Zm8=");
        assert_eq!(encode_base64(b"foo"), "Zm9v");
        assert_eq!(encode_base64(b"foobar"), "Zm9vYmFy");
    }

    #[test]
    fn json_string_escapes_control_and_unit_separator_chars() {
        let escaped = json_string("a\"b\\c\u{001F}\n");
        assert_eq!(escaped, r#""a\"b\\c\u001f\n""#);
    }

    #[test]
    fn thumbnail_fingerprint_is_deterministic_and_nonzero_for_nonempty_payload() {
        assert_eq!(compute_thumbnail_fingerprint(&[]), 0);
        let fp1 = compute_thumbnail_fingerprint(&[1, 2, 3, 4, 5, 6, 7, 8, 9]);
        let fp2 = compute_thumbnail_fingerprint(&[1, 2, 3, 4, 5, 6, 7, 8, 9]);
        let fp3 = compute_thumbnail_fingerprint(&[1, 2, 3, 4, 5, 6, 7, 8, 10]);
        assert_ne!(fp1, 0);
        assert_eq!(fp1, fp2);
        assert_ne!(fp1, fp3);
    }

    #[test]
    fn candidate_selection_prefers_playing_system_current_then_any_playing() {
        let candidates = [
            SystemSessionCandidate {
                state: PlaybackState::Paused,
                is_closed: false,
                is_system_current: true,
                is_previous_current: true,
            },
            SystemSessionCandidate {
                state: PlaybackState::Playing,
                is_closed: false,
                is_system_current: false,
                is_previous_current: false,
            },
        ];
        assert_eq!(select_system_session_candidate_index(&candidates), Some(1));
    }
}
