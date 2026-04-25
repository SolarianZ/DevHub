use crate::models::BootstrapSnapshot;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, RwLock};
use tauri::{AppHandle, Emitter};

#[derive(Clone)]
pub struct SnapshotPublisher {
    snapshot: Arc<RwLock<BootstrapSnapshot>>,
    generation: Arc<AtomicU64>,
    exit_requested: Arc<AtomicBool>,
}

impl SnapshotPublisher {
    pub fn new(initial_snapshot: BootstrapSnapshot) -> Self {
        let generation = initial_snapshot.generation;
        Self {
            snapshot: Arc::new(RwLock::new(initial_snapshot)),
            generation: Arc::new(AtomicU64::new(generation)),
            exit_requested: Arc::new(AtomicBool::new(false)),
        }
    }

    pub fn current(&self) -> BootstrapSnapshot {
        self.snapshot
            .read()
            .expect("snapshot lock poisoned")
            .clone()
    }

    pub fn publish(&self, app: &AppHandle, snapshot: BootstrapSnapshot) {
        self.replace_current(snapshot.clone());
        let _ = app.emit(crate::models::EVENT_BOOTSTRAP_STATE_CHANGED, snapshot);
    }

    pub fn publish_if_current(
        &self,
        app: &AppHandle,
        expected_generation: u64,
        snapshot: BootstrapSnapshot,
    ) -> bool {
        if !self.try_replace_current(expected_generation, snapshot.clone()) {
            return false;
        }

        let _ = app.emit(crate::models::EVENT_BOOTSTRAP_STATE_CHANGED, snapshot);
        true
    }

    pub fn update_current<F>(&self, app: &AppHandle, update: F)
    where
        F: FnOnce(&mut BootstrapSnapshot),
    {
        let mut snapshot = self.current();
        update(&mut snapshot);
        self.publish(app, snapshot);
    }

    pub fn advance_generation(&self) -> u64 {
        self.generation.fetch_add(1, Ordering::SeqCst) + 1
    }

    pub fn is_current_generation(&self, generation: u64) -> bool {
        self.generation.load(Ordering::SeqCst) == generation
    }

    pub fn should_exit(&self) -> bool {
        self.exit_requested.load(Ordering::SeqCst)
    }

    pub fn request_exit(&self) {
        self.exit_requested.store(true, Ordering::SeqCst);
    }

    fn replace_current(&self, snapshot: BootstrapSnapshot) {
        *self.snapshot.write().expect("snapshot lock poisoned") = snapshot;
    }

    fn try_replace_current(&self, expected_generation: u64, snapshot: BootstrapSnapshot) -> bool {
        if self.generation.load(Ordering::SeqCst) != expected_generation {
            return false;
        }

        let mut current = self.snapshot.write().expect("snapshot lock poisoned");
        if self.generation.load(Ordering::SeqCst) != expected_generation {
            return false;
        }

        *current = snapshot;
        true
    }
}

#[cfg(test)]
mod tests {
    use super::SnapshotPublisher;
    use crate::models::{BootstrapPhase, BootstrapSnapshot, DataDirSource, MonitorSettings};

    fn create_snapshot(generation: u64) -> BootstrapSnapshot {
        BootstrapSnapshot {
            generation,
            phase: BootstrapPhase::Scanning,
            effective_data_dir: "/tmp/devhub".to_string(),
            data_dir_source: DataDirSource::SettingsOverride,
            settings: MonitorSettings::default(),
            has_configured_host_executable: false,
            connection: None,
            last_problem: None,
        }
    }

    #[test]
    fn try_replace_current_rejects_stale_generation() {
        let publisher = SnapshotPublisher::new(create_snapshot(1));
        publisher.advance_generation();

        let rejected = publisher.try_replace_current(1, create_snapshot(1));
        assert!(!rejected);
        assert_eq!(publisher.current().generation, 1);
    }

    #[test]
    fn try_replace_current_updates_matching_generation() {
        let publisher = SnapshotPublisher::new(create_snapshot(1));
        let generation = publisher.advance_generation();
        let mut snapshot = create_snapshot(generation);
        snapshot.phase = BootstrapPhase::LaunchAvailable;

        let accepted = publisher.try_replace_current(generation, snapshot.clone());
        assert!(accepted);
        assert!(matches!(publisher.current().phase, BootstrapPhase::LaunchAvailable));
    }
}
