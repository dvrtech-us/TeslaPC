//! SQLite playback history. The watched flag is decided by [`crate::decisions::history::watched_now`].

use std::collections::HashMap;

use rusqlite::{params, Connection};

use crate::decisions::history::watched_now;
use crate::decisions::listing::FileProgress;
use crate::paths;

pub struct Library {
    conn: Connection,
}

#[derive(Debug, Clone)]
pub struct Progress {
    pub position: f64,
    pub duration: f64,
    pub watched: bool,
    pub play_count: i32,
}

impl Library {
    pub fn open() -> Result<Self, String> {
        let path = paths::db_file();
        if let Some(dir) = path.parent() {
            std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
        }
        let conn = Connection::open(&path).map_err(|e| e.to_string())?;
        conn.execute_batch(
            "CREATE TABLE IF NOT EXISTS playback (
                path        TEXT PRIMARY KEY COLLATE NOCASE,
                position    REAL NOT NULL DEFAULT 0,
                duration    REAL NOT NULL DEFAULT 0,
                watched     INTEGER NOT NULL DEFAULT 0,
                play_count  INTEGER NOT NULL DEFAULT 0,
                updated_at  TEXT
            );",
        )
        .map_err(|e| e.to_string())?;
        println!("[Library] SQLite playback DB at {}", path.display());
        Ok(Self { conn })
    }

    pub fn get(&self, path: &str) -> Option<Progress> {
        let mut stmt = self
            .conn
            .prepare("SELECT position, duration, watched, play_count FROM playback WHERE path = ?1")
            .ok()?;
        stmt.query_row(params![path], |row| {
            Ok(Progress {
                position: row.get(0)?,
                duration: row.get(1)?,
                watched: row.get::<_, i64>(2)? != 0,
                play_count: row.get(3)?,
            })
        })
        .ok()
    }

    pub fn get_all(&self) -> HashMap<String, FileProgress> {
        let mut map = HashMap::new();
        let mut stmt = match self
            .conn
            .prepare("SELECT path, position, duration, watched, play_count FROM playback")
        {
            Ok(stmt) => stmt,
            Err(err) => {
                println!("[Library] GetAll failed: {err}");
                return map;
            }
        };
        let rows = stmt.query_map([], |row| {
            let path: String = row.get(0)?;
            let position: f64 = row.get(1)?;
            let duration: f64 = row.get(2)?;
            let watched: i64 = row.get(3)?;
            Ok((path, position, duration, watched != 0))
        });
        if let Ok(rows) = rows {
            for row in rows.flatten() {
                map.insert(
                    row.0.to_ascii_lowercase(),
                    FileProgress {
                        position: row.1,
                        duration: row.2,
                        watched: row.3,
                    },
                );
            }
        }
        map
    }

    pub fn save_progress(&self, path: &str, position: f64, duration: f64) {
        if path.trim().is_empty() {
            return;
        }
        let already = self.get(path).map(|p| p.watched).unwrap_or(false);
        let watched = watched_now(position, duration, already);
        let now = chrono::Utc::now().to_rfc3339();
        if let Err(err) = self.conn.execute(
            "INSERT INTO playback (path, position, duration, watched, play_count, updated_at)
             VALUES (?1, ?2, ?3, ?4, 0, ?5)
             ON CONFLICT(path) DO UPDATE SET
               position = ?2,
               duration = CASE WHEN ?3 > 0 THEN ?3 ELSE duration END,
               watched = MAX(watched, ?4),
               updated_at = ?5",
            params![path, position, duration, watched as i32, now],
        ) {
            println!("[Library] SaveProgress failed: {err}");
        }
    }

    pub fn mark_watched(&self, path: &str, duration: f64) {
        if duration > 0.0 {
            self.save_progress(path, duration, duration);
        }
    }

    pub fn increment_play_count(&self, path: &str) {
        if path.trim().is_empty() {
            return;
        }
        let now = chrono::Utc::now().to_rfc3339();
        if let Err(err) = self.conn.execute(
            "INSERT INTO playback (path, play_count, updated_at) VALUES (?1, 1, ?2)
             ON CONFLICT(path) DO UPDATE SET play_count = play_count + 1, updated_at = ?2",
            params![path, now],
        ) {
            println!("[Library] IncrementPlayCount failed: {err}");
        }
    }
}

