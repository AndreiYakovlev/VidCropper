import { test } from 'node:test';
import assert from 'node:assert/strict';
import { formatProcessingTime, processingTimeText } from '../wwwroot/js/processing-time.mjs';

test('processing time formats hours without wrapping after 24 hours', () => {
  for (const [seconds, expected] of [[0,'00:00:00'],[59.9,'00:00:59'],[60,'00:01:00'],[3661,'01:01:01'],[360000,'100:00:00']])
    assert.equal(formatProcessingTime(seconds), expected);
});
test('ETA is explicitly approximate and omitted after completion', () => {
  assert.equal(processingTimeText({status:'running',elapsedSeconds:3,remainingSeconds:null}), 'До конца этапа: расчёт… · Общее время: 00:00:03');
  assert.equal(processingTimeText({status:'running',elapsedSeconds:3,remainingSeconds:1.2}), 'До конца этапа: ≈ 00:00:02 · Общее время: 00:00:03');
  assert.equal(processingTimeText({status:'completed',elapsedSeconds:65,remainingSeconds:0}), 'Общее время: 00:01:05');
});
