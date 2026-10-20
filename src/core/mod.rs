//! The backup logic, independent of any UI or OS: settings and drone list, talking to the flight controller,
//! and the engine that decides when to back up.

pub mod engine;
pub mod fc;
pub mod store;
