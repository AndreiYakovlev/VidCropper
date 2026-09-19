import { setupPlayer } from './player.js';
import { setupCrop } from './crop.js';
import { setupSettings } from './settings.js';
import { setupBackend } from './backend.js';

const video = setupPlayer();
setupCrop(video);
setupSettings();
setupBackend();
