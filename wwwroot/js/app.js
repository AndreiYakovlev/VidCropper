import { setupPlayer } from './player.js';
import { setupCrop } from './crop.js';
import { setupSettings } from './settings.js';
import { setupBackend } from './backend.js';
import { setupTrim } from './trim-control.js';

const video = setupPlayer();
setupTrim(video);
setupCrop(video);
setupSettings();
setupBackend();
