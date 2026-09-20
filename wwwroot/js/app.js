import { setupUpscaler } from './upscaler.js';
import { setupPlayer } from './player.js';
import { setupCrop } from './crop.js';
import { setupSettings } from './settings.js';
import { setupBackend } from './backend.js';
import { setupTrim } from './trim-control.js';
import { setupLinkDialog } from './link-dialog.js';

const { video, openRemote } = setupPlayer();
setupTrim(video);
setupCrop(video);
setupSettings();
setupBackend();
setupLinkDialog(openRemote);

setupUpscaler(video);
