CREATE TABLE work_items (
    id TEXT PRIMARY KEY NOT NULL,
    title TEXT NOT NULL,
    created_at TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('open', 'completed')),
    completed_at TEXT NULL,
    CHECK (
        (state = 'open' AND completed_at IS NULL)
        OR (state = 'completed' AND completed_at IS NOT NULL)
    )
);
