// ESLint flat config (ESLint v9+).
// Lints the small set of Node.js scripts that ship in this repo:
//   - hooks/*.js   (Claude Code hook scripts)
//   - scripts/*.js (CLI helpers, statusline, etc.)
//
// Embedded subprojects with their own dependency trees (mcp-session-history)
// are ignored because they manage their own tooling.

import js from '@eslint/js';
import globals from 'globals';

export default [
    {
        ignores: [
            '**/node_modules/**',
            '**/bin/**',
            '**/obj/**',
            '**/staged/**',
            'mcp-session-history/**',
            'installer/output/**',
            '.codesight/**',
        ],
    },

    // Base recommended rules from @eslint/js.
    js.configs.recommended,

    // Node.js (CommonJS) scripts.
    {
        files: ['hooks/**/*.js', 'scripts/**/*.js'],
        languageOptions: {
            ecmaVersion: 2024,
            sourceType: 'commonjs',
            globals: {
                ...globals.node,
            },
        },
        rules: {
            // Hook + CLI scripts legitimately log to console / stderr.
            'no-console': 'off',

            // Unused variables are a real defect signal.
            'no-unused-vars': ['error', {
                argsIgnorePattern: '^_',
                varsIgnorePattern: '^_',
                caughtErrorsIgnorePattern: '^_',
            }],

            // Mistyped Promise / undefined shows up as no-undef from recommended; keep it on.
            'no-empty': ['error', { allowEmptyCatch: true }],
            'no-prototype-builtins': 'off',
        },
    },

    // ESM modules (eslint config itself, future *.mjs scripts).
    {
        files: ['**/*.mjs'],
        languageOptions: {
            ecmaVersion: 2024,
            sourceType: 'module',
            globals: {
                ...globals.node,
            },
        },
    },
];
