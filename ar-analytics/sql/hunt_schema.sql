-- ============================================================
-- AR Meme Hunt — challenge engine tables
-- NOTE: api/controllers/hunt.php auto-creates these tables on
-- first request, so running this file manually is OPTIONAL.
-- Kept here as reference / for fresh installs.
-- ============================================================

-- ─── Participants ───
-- Phone number is the unique participant identifier (anti-cheat).
-- Timestamps started_at/completed_at use DATETIME(3) for millisecond
-- precision so leaderboard ties are meaningful.
CREATE TABLE IF NOT EXISTS hunt_participants (
    id INT AUTO_INCREMENT PRIMARY KEY,
    name VARCHAR(100) NOT NULL,
    phone VARCHAR(20) NOT NULL,               -- normalized digits only
    company VARCHAR(150) DEFAULT '',
    business_type VARCHAR(100) DEFAULT '',
    token VARCHAR(64) NOT NULL,               -- session token (localStorage/URL)
    consent BOOLEAN DEFAULT TRUE,
    registered_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    started_at DATETIME(3) NULL,              -- set on Start Scanning (or first scan)
    completed_at DATETIME(3) NULL,            -- set when 5th poster is scanned
    total_ms BIGINT NULL,                     -- completed_at - started_at in ms
    is_verified BOOLEAN DEFAULT FALSE,        -- manual winner verification
    UNIQUE KEY uk_phone (phone),
    UNIQUE KEY uk_token (token),
    INDEX idx_completed (completed_at)
) ENGINE=InnoDB;

-- ─── Scans ───
-- One row per (participant, poster); the unique key makes double
-- scanning the same poster impossible at the database level.
CREATE TABLE IF NOT EXISTS hunt_scans (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    participant_id INT NOT NULL,
    poster_id VARCHAR(64) NOT NULL,           -- Unity image target id
    scanned_at DATETIME(3) NULL,              -- server-side ms timestamp
    UNIQUE KEY uk_participant_poster (participant_id, poster_id),
    INDEX idx_poster (poster_id),
    FOREIGN KEY (participant_id) REFERENCES hunt_participants(id) ON DELETE CASCADE
) ENGINE=InnoDB;
