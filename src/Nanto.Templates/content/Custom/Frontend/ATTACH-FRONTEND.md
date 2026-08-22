# Attach a frontend

Place or create the SPA in this directory, then edit `nanto.json`:

- add `frontend.install` when dependency restore needs a command;
- add `frontend.dev.file` and `frontend.dev.arguments` when Nanto should own the development server, or leave both absent for an externally started server;
- set the exact development URL and port;
- set the production build command, distribution directory, and entry asset;
- alias `@nanto/app` to `src/generated/nanto/app/index.ts` and `@nanto/core` to `src/generated/nanto/core/index.ts`.

No Nanto runtime changes are required.
