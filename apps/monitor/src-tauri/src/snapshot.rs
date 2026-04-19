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
        *self.snapshot.write().expect("snapshot lock poisoned") = snapshot.clone();
        let _ = app.emit(crate::models::EVENT_BOOTSTRAP_STATE_CHANGED, snapshot);
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
}
