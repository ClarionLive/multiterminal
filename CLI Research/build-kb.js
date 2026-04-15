#!/usr/bin/env node
/**
 * build-kb.js — Parse Claude Code DOCUMENTATION.md into a SQLite knowledge base.
 *
 * Usage:
 *   node build-kb.js
 *
 * Reads:  ./sources/DOCUMENTATION.md
 * Writes: ./claude-code-kb.db
 *
 * Dependencies: better-sqlite3 (npm install better-sqlite3)
 */

const fs = require('fs');
const path = require('path');
const Database = require('better-sqlite3');

const SOURCES_DIR = path.join(__dirname, 'sources');
const DB_PATH = path.join(__dirname, 'claude-code-kb.db');
const DOC_PATH = path.join(SOURCES_DIR, 'DOCUMENTATION.md');

// ---------------------------------------------------------------------------
// Schema
// ---------------------------------------------------------------------------

const SCHEMA = `
CREATE TABLE IF NOT EXISTS cc_kb (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    section TEXT NOT NULL,
    subsection TEXT,
    heading TEXT NOT NULL,
    content TEXT NOT NULL,
    source TEXT NOT NULL DEFAULT 'DOCUMENTATION.md',
    chunk_index INTEGER NOT NULL DEFAULT 0,
    created_at TEXT DEFAULT (datetime('now'))
);

CREATE VIRTUAL TABLE IF NOT EXISTS cc_kb_fts USING fts5(
    section, subsection, heading, content,
    content='cc_kb', content_rowid='id',
    tokenize='porter'
);

-- Keep FTS index in sync
CREATE TRIGGER IF NOT EXISTS cc_kb_ai AFTER INSERT ON cc_kb BEGIN
    INSERT INTO cc_kb_fts(rowid, section, subsection, heading, content)
    VALUES (new.id, new.section, new.subsection, new.heading, new.content);
END;

CREATE TRIGGER IF NOT EXISTS cc_kb_ad AFTER DELETE ON cc_kb BEGIN
    INSERT INTO cc_kb_fts(cc_kb_fts, rowid, section, subsection, heading, content)
    VALUES ('delete', old.id, old.section, old.subsection, old.heading, old.content);
END;

CREATE TRIGGER IF NOT EXISTS cc_kb_au AFTER UPDATE ON cc_kb BEGIN
    INSERT INTO cc_kb_fts(cc_kb_fts, rowid, section, subsection, heading, content)
    VALUES ('delete', old.id, old.section, old.subsection, old.heading, old.content);
    INSERT INTO cc_kb_fts(rowid, section, subsection, heading, content)
    VALUES (new.id, new.section, new.subsection, new.heading, new.content);
END;
`;

// ---------------------------------------------------------------------------
// Markdown Parser
// ---------------------------------------------------------------------------

/**
 * Parse DOCUMENTATION.md into chunks based on heading hierarchy.
 * Each chunk is a { section, subsection, heading, content } object.
 */
function parseMarkdown(text) {
    const lines = text.split('\n');
    const chunks = [];

    let currentSection = '';      // H2 (## )
    let currentSubsection = '';   // H3 (### )
    let currentHeading = '';      // H4 (#### ) or same as subsection/section
    let buffer = [];
    let chunkIndex = 0;

    function flushBuffer() {
        const content = buffer.join('\n').trim();
        if (content && currentSection) {
            chunks.push({
                section: currentSection,
                subsection: currentSubsection || null,
                heading: currentHeading || currentSubsection || currentSection,
                content,
                chunkIndex: chunkIndex++
            });
        }
        buffer = [];
    }

    for (let i = 0; i < lines.length; i++) {
        const line = lines[i];

        // H2: New top-level section
        if (line.startsWith('## ') && !line.startsWith('### ')) {
            flushBuffer();
            currentSection = line.replace(/^## /, '').replace(/\{.*\}/, '').trim();
            currentSubsection = '';
            currentHeading = currentSection;
            continue;
        }

        // H3: New subsection
        if (line.startsWith('### ')) {
            flushBuffer();
            currentSubsection = line.replace(/^### /, '').replace(/\{.*\}/, '').trim();
            currentHeading = currentSubsection;
            continue;
        }

        // H4: Sub-subsection heading
        if (line.startsWith('#### ')) {
            flushBuffer();
            currentHeading = line.replace(/^#### /, '').replace(/\{.*\}/, '').trim();
            continue;
        }

        // Skip the document title (H1) and front-matter
        if (line.startsWith('# ') && !line.startsWith('## ')) {
            continue;
        }

        // Accumulate content
        if (currentSection) {
            buffer.push(line);
        }
    }

    // Flush remaining
    flushBuffer();

    return chunks;
}

/**
 * Post-process chunks: split overly large chunks (>2000 chars) at paragraph boundaries.
 */
function splitLargeChunks(chunks, maxSize = 2000) {
    const result = [];
    let globalIndex = 0;

    for (const chunk of chunks) {
        if (chunk.content.length <= maxSize) {
            result.push({ ...chunk, chunkIndex: globalIndex++ });
            continue;
        }

        // Split at double-newlines (paragraph boundaries)
        const paragraphs = chunk.content.split(/\n\n+/);
        let currentContent = '';

        for (const para of paragraphs) {
            if (currentContent && (currentContent.length + para.length + 2) > maxSize) {
                result.push({
                    ...chunk,
                    content: currentContent.trim(),
                    chunkIndex: globalIndex++
                });
                currentContent = para;
            } else {
                currentContent += (currentContent ? '\n\n' : '') + para;
            }
        }

        if (currentContent.trim()) {
            result.push({
                ...chunk,
                content: currentContent.trim(),
                chunkIndex: globalIndex++
            });
        }
    }

    return result;
}

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------

function main() {
    console.log('Building Claude Code knowledge base...\n');

    // Read source
    if (!fs.existsSync(DOC_PATH)) {
        console.error(`ERROR: Source file not found: ${DOC_PATH}`);
        console.error('Run the download step first.');
        process.exit(1);
    }

    const markdown = fs.readFileSync(DOC_PATH, 'utf-8');
    console.log(`Read ${markdown.length.toLocaleString()} chars from DOCUMENTATION.md`);

    // Parse into chunks
    let chunks = parseMarkdown(markdown);
    console.log(`Parsed ${chunks.length} raw chunks`);

    // Split large chunks
    chunks = splitLargeChunks(chunks);
    console.log(`After splitting: ${chunks.length} chunks`);

    // Print section distribution
    const sectionCounts = {};
    for (const c of chunks) {
        sectionCounts[c.section] = (sectionCounts[c.section] || 0) + 1;
    }
    console.log('\nSection distribution:');
    for (const [section, count] of Object.entries(sectionCounts)) {
        console.log(`  ${section}: ${count} chunks`);
    }

    // Remove old DB
    if (fs.existsSync(DB_PATH)) {
        fs.unlinkSync(DB_PATH);
        console.log('\nRemoved old database');
    }

    // Create database
    const db = new Database(DB_PATH);
    db.pragma('journal_mode = WAL');

    // Create schema
    db.exec(SCHEMA);
    console.log('Created schema (cc_kb + cc_kb_fts + triggers)');

    // Insert chunks
    const insert = db.prepare(`
        INSERT INTO cc_kb (section, subsection, heading, content, source, chunk_index)
        VALUES (@section, @subsection, @heading, @content, @source, @chunkIndex)
    `);

    const insertMany = db.transaction((chunks) => {
        for (const chunk of chunks) {
            insert.run({
                section: chunk.section,
                subsection: chunk.subsection,
                heading: chunk.heading,
                content: chunk.content,
                source: 'DOCUMENTATION.md',
                chunkIndex: chunk.chunkIndex
            });
        }
    });

    insertMany(chunks);
    console.log(`Inserted ${chunks.length} chunks into cc_kb`);

    // Verify FTS5
    const ftsCount = db.prepare('SELECT count(*) as cnt FROM cc_kb_fts').get();
    console.log(`FTS5 index: ${ftsCount.cnt} entries`);

    // Sample search test
    console.log('\n--- Sample Search: "tool" ---');
    const results = db.prepare(`
        SELECT id, section, subsection, heading, length(content) as len
        FROM cc_kb
        WHERE id IN (
            SELECT rowid FROM cc_kb_fts WHERE cc_kb_fts MATCH '"tool"'
        )
        LIMIT 5
    `).all();
    for (const r of results) {
        console.log(`  [${r.id}] ${r.section} > ${r.heading} (${r.len} chars)`);
    }

    console.log('\n--- Sample Search: "Kairos" ---');
    const kairos = db.prepare(`
        SELECT id, section, heading, length(content) as len
        FROM cc_kb
        WHERE id IN (
            SELECT rowid FROM cc_kb_fts WHERE cc_kb_fts MATCH '"Kairos"'
        )
        LIMIT 5
    `).all();
    for (const r of kairos) {
        console.log(`  [${r.id}] ${r.section} > ${r.heading} (${r.len} chars)`);
    }

    console.log('\n--- Sample Search: "state management zustand" ---');
    const state = db.prepare(`
        SELECT id, section, heading, length(content) as len
        FROM cc_kb
        WHERE id IN (
            SELECT rowid FROM cc_kb_fts WHERE cc_kb_fts MATCH 'state management zustand'
        )
        LIMIT 5
    `).all();
    for (const r of state) {
        console.log(`  [${r.id}] ${r.section} > ${r.heading} (${r.len} chars)`);
    }

    // Stats
    const stats = db.prepare(`
        SELECT
            count(*) as total,
            avg(length(content)) as avg_len,
            min(length(content)) as min_len,
            max(length(content)) as max_len,
            sum(length(content)) as total_chars
        FROM cc_kb
    `).get();

    console.log(`\n=== Final Stats ===`);
    console.log(`Total chunks: ${stats.total}`);
    console.log(`Avg chunk size: ${Math.round(stats.avg_len)} chars`);
    console.log(`Min/Max: ${stats.min_len} / ${stats.max_len} chars`);
    console.log(`Total content: ${stats.total_chars.toLocaleString()} chars`);
    console.log(`Database: ${DB_PATH}`);

    db.close();
    console.log('\nDone!');
}

main();
