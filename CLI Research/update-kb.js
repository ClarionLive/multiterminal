#!/usr/bin/env node
/**
 * update-kb.js — Incrementally add Claude Code changelog/release notes to the KB.
 *
 * Usage:
 *   node update-kb.js --version "2.1.90" --text "changelog text..."
 *   node update-kb.js --version "2.1.90" --file ./changelog.md
 *   echo "changelog text" | node update-kb.js --version "2.1.90" --stdin
 *
 * Reads/writes: ./claude-code-kb.db
 *
 * This script NEVER deletes existing data. It only appends new chunks
 * and updates the last_known_version in cc_metadata.
 *
 * Dependencies: better-sqlite3
 */

const fs = require('fs');
const path = require('path');
const Database = require('better-sqlite3');

const DB_PATH = path.join(__dirname, 'claude-code-kb.db');

// ---------------------------------------------------------------------------
// Args
// ---------------------------------------------------------------------------

function parseArgs() {
    const args = process.argv.slice(2);
    const opts = { version: null, text: null, file: null, stdin: false, dryRun: false };

    for (let i = 0; i < args.length; i++) {
        switch (args[i]) {
            case '--version': opts.version = args[++i]; break;
            case '--text': opts.text = args[++i]; break;
            case '--file': opts.file = args[++i]; break;
            case '--stdin': opts.stdin = true; break;
            case '--dry-run': opts.dryRun = true; break;
            default:
                console.error(`Unknown arg: ${args[i]}`);
                process.exit(1);
        }
    }

    if (!opts.version) {
        console.error('ERROR: --version is required');
        console.error('Usage: node update-kb.js --version "2.1.90" --text "..." | --file path | --stdin');
        process.exit(1);
    }

    return opts;
}

async function getInputText(opts) {
    if (opts.text) return opts.text;
    if (opts.file) return fs.readFileSync(opts.file, 'utf-8');
    if (opts.stdin) {
        const chunks = [];
        for await (const chunk of process.stdin) chunks.push(chunk);
        return Buffer.concat(chunks).toString('utf-8');
    }
    console.error('ERROR: one of --text, --file, or --stdin is required');
    process.exit(1);
}

// ---------------------------------------------------------------------------
// Chunking — same logic as build-kb.js but simpler for changelogs
// ---------------------------------------------------------------------------

function chunkChangelog(version, text) {
    const lines = text.split('\n');
    const chunks = [];
    let currentHeading = `v${version} Release Notes`;
    let buffer = [];

    function flush() {
        const content = buffer.join('\n').trim();
        if (content) {
            chunks.push({
                section: `Changelog v${version}`,
                subsection: null,
                heading: currentHeading,
                content,
                source: `changelog-v${version}`
            });
        }
        buffer = [];
    }

    for (const line of lines) {
        // Any markdown heading starts a new chunk
        if (/^#{1,4}\s/.test(line)) {
            flush();
            currentHeading = line.replace(/^#+\s*/, '').trim();
            continue;
        }
        buffer.push(line);
    }
    flush();

    // Split oversized chunks (>2000 chars) at paragraph boundaries
    const result = [];
    for (const chunk of chunks) {
        if (chunk.content.length <= 2000) {
            result.push(chunk);
            continue;
        }
        const paragraphs = chunk.content.split(/\n\n+/);
        let current = '';
        for (const para of paragraphs) {
            if (current && (current.length + para.length + 2) > 2000) {
                result.push({ ...chunk, content: current.trim() });
                current = para;
            } else {
                current += (current ? '\n\n' : '') + para;
            }
        }
        if (current.trim()) {
            result.push({ ...chunk, content: current.trim() });
        }
    }

    return result;
}

// ---------------------------------------------------------------------------
// Database
// ---------------------------------------------------------------------------

function ensureSchema(db) {
    // Ensure metadata table exists (idempotent)
    db.exec(`
        CREATE TABLE IF NOT EXISTS cc_metadata (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at TEXT DEFAULT (datetime('now'))
        );
    `);
}

function getLastVersion(db) {
    const row = db.prepare('SELECT value FROM cc_metadata WHERE key = ?').get('last_known_version');
    return row?.value || null;
}

function setLastVersion(db, version) {
    db.prepare(
        'INSERT OR REPLACE INTO cc_metadata (key, value, updated_at) VALUES (?, ?, datetime(\'now\'))'
    ).run('last_known_version', version);
}

function versionAlreadyIngested(db, version) {
    const row = db.prepare(
        'SELECT count(*) as cnt FROM cc_kb WHERE source = ?'
    ).get(`changelog-v${version}`);
    return row.cnt > 0;
}

function insertChunks(db, chunks) {
    const insert = db.prepare(`
        INSERT INTO cc_kb (section, subsection, heading, content, source, chunk_index)
        VALUES (@section, @subsection, @heading, @content, @source, @chunkIndex)
    `);

    const tx = db.transaction((items) => {
        items.forEach((chunk, i) => {
            insert.run({
                section: chunk.section,
                subsection: chunk.subsection,
                heading: chunk.heading,
                content: chunk.content,
                source: chunk.source,
                chunkIndex: i
            });
        });
    });

    tx(chunks);
}

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------

async function main() {
    const opts = parseArgs();
    const text = await getInputText(opts);
    const version = opts.version.replace(/^v/, ''); // normalize: strip leading 'v'

    console.log(`Processing changelog for v${version} (${text.length} chars)`);

    const db = new Database(DB_PATH);
    db.pragma('journal_mode = WAL');
    ensureSchema(db);

    // Dedup check
    if (versionAlreadyIngested(db, version)) {
        console.log(`v${version} already in KB — skipping (${db.prepare('SELECT count(*) as c FROM cc_kb WHERE source = ?').get(`changelog-v${version}`).c} existing chunks)`);
        db.close();
        process.exit(0);
    }

    // Chunk the changelog
    const chunks = chunkChangelog(version, text);
    console.log(`Chunked into ${chunks.length} pieces`);

    if (opts.dryRun) {
        console.log('\n--- DRY RUN (no DB writes) ---');
        chunks.forEach((c, i) => {
            console.log(`[${i}] ${c.heading} (${c.content.length} chars)`);
        });
        db.close();
        return;
    }

    // Insert
    insertChunks(db, chunks);
    console.log(`Inserted ${chunks.length} chunks into cc_kb`);

    // Update version
    const prevVersion = getLastVersion(db);
    setLastVersion(db, version);
    console.log(`Updated last_known_version: ${prevVersion} → ${version}`);

    // Verify
    const total = db.prepare('SELECT count(*) as c FROM cc_kb').get();
    const fts = db.prepare('SELECT count(*) as c FROM cc_kb_fts').get();
    console.log(`Total KB: ${total.c} chunks (FTS: ${fts.c})`);

    db.close();
    console.log('Done!');
}

main().catch(err => {
    console.error('FATAL:', err.message);
    process.exit(1);
});
