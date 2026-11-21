import { seeded } from './content.js';

// Every unit test file starts the fake GitHub, seeded from content-configurations/, before it runs.
await seeded();
