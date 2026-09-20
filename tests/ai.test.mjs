import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { upscaleSize, previewRange } from '../wwwroot/js/upscale.mjs';

test('AI output scale is independent of reduction; odd crop rounds down to even pixels', () => {
  assert.deepEqual(upscaleSize({width:101,height:57},3),{width:302,height:170});
  assert.deepEqual(upscaleSize({width:720,height:1280},2),{width:1440,height:2560});
});
test('preview stays inside trim and falls back at its end', () => {
  assert.deepEqual(previewRange({start:10,end:20},12),{start:12,end:15});
  assert.deepEqual(previewRange({start:10,end:20},20),{start:17,end:20});
  assert.deepEqual(previewRange({start:10,end:11},11),{start:10,end:11});
});

const root = resolve(import.meta.dirname,'..');
const headers = {'X-VidCropper':'1','Content-Type':'application/json'};
function ffmpeg(args) {
  const r=spawnSync('ffmpeg',['-hide_banner','-loglevel','error','-y',...args],{encoding:'utf8'});
  assert.equal(r.status,0,r.stderr);
}

test('real GPU: five models, all scales, trim, preview, cancellation and exclusion',
  {skip:process.env.VIDCROPPER_GPU_TESTS!=='1',timeout:600000}, async t => {
  const fixtures=join(root,'artifacts/ai-tests'); await mkdir(fixtures,{recursive:true});
  const sourcePath=join(fixtures,'source.mp4');
  ffmpeg(['-f','lavfi','-i','testsrc2=size=64x48:rate=24','-f','lavfi','-i','sine=frequency=880:sample_rate=48000',
    '-t','1','-c:v','libx264','-c:a','aac',sourcePath]);
  const server=process.env.VIDCROPPER_TEST_URL ? null : spawn('dotnet',[join(root,'artifacts/backend-build/VidCropper.dll'),'--port','0'],{cwd:root,windowsHide:true});
  let logs='';
  const url=process.env.VIDCROPPER_TEST_URL || await new Promise((ok,fail)=>{
    server.stdout.on('data',d=>{logs+=d;const m=logs.match(/VidCropper: (http:\/\/127\.0\.0\.1:\d+)/);if(m)ok(m[1]);});
    server.stderr.on('data',d=>logs+=d);server.on('exit',c=>fail(new Error(`${c}: ${logs}`)));
  });
  async function api(path,method='GET',body){
    const r=await fetch(url+path,{method,headers,body:body===undefined?undefined:JSON.stringify(body)});
    const value=r.status===204?null:await r.json(); assert.ok(r.ok,JSON.stringify(value));return value;
  }
  async function finish(job){
    const response=await fetch(`${url}/api/exports/${job.id}/events`);
    const values=(await response.text()).trim().split('\n\n').map(s=>JSON.parse(s.slice(6)));
    const end=values.at(-1);assert.equal(end.status,'completed',JSON.stringify(end));
    for(let i=1;i<values.length;i++)assert.ok(values[i].progress>=values[i-1].progress);
    for(const value of values.filter(v=>v.stageId)) {
      assert.ok(['extract','upscale','encode','compare'].includes(value.stageId));
      assert.ok(value.stageProgress>=0 && value.stageProgress<=100);
      assert.ok(value.framesDone<=value.framesTotal);
    }
    return end;
  }
  let media;
  try {
    for(const model of ['nomos-weak','realesrgan']){
      let op=await api(`/api/ai/models/${model}/install`,'POST');
      while(!['completed','failed','cancelled'].includes(op.status)){
        await new Promise(r=>setTimeout(r,200));op=await api(`/api/ai/operations/${op.id}`);
      }
      assert.equal(op.status,'completed',JSON.stringify(op));
    }
    const uploadId=crypto.randomUUID();
    const response=await fetch(`${url}/api/media/${uploadId}?name=ai-source.mp4`,{method:'PUT',headers:{'X-VidCropper':'1','Content-Type':'application/octet-stream'},body:await readFile(sourcePath)});
    assert.equal(response.status,200); media=await response.json();
    const request={mediaId:media.id,crop:{x:1,y:1,width:61,height:45},sourceWidth:64,sourceHeight:48,scale:10,fps:24,audio:true,quality:'balanced',startSeconds:0,endSeconds:1};
    for(const modelId of ['nomos-weak','nomos-medium','nomos-strong','realesrgan','anime-video'])for(const scale of [2,3,4]){
      await t.test(`${modelId} ×${scale}`,async()=>{
        const job=await finish(await api('/api/exports','POST',{...request,upscale:{modelId,scale}}));
        assert.equal(job.result.width,Math.floor(61*scale/2)*2);assert.equal(job.result.height,Math.floor(45*scale/2)*2);
        assert.equal(job.result.fps,24);assert.equal(job.result.hasAudio,true);assert.ok(Math.abs(job.result.duration-1)<.1);
        assert.equal(job.framesDone,24);assert.equal(job.framesTotal,24);
        const data=Buffer.from(await (await fetch(`${url}/api/exports/${job.id}/download`)).arrayBuffer());
        assert.ok(data.toString('latin1').includes('crf=23.0'));
        await writeFile(join(fixtures,`${modelId}-${scale}.mp4`),data);
        await api(`/api/exports/${job.id}`,'DELETE');
      });
    }
    await t.test('preview at trim end; before/after routes and cleanup',async()=>{
      const job=await finish(await api('/api/ai/previews','POST',{export:{...request,upscale:{modelId:'nomos-weak',scale:2}},position:1}));
      assert.equal(job.preview,true);assert.equal(job.result.hasAudio,false);
      for(const variant of ['before','after'])assert.equal((await fetch(`${url}/api/ai/previews/${job.id}/${variant}`)).status,200);
      await api(`/api/exports/${job.id}`,'DELETE');
      assert.equal((await fetch(`${url}/api/ai/previews/${job.id}/after`)).status,404);
    });
    await t.test('fractional trim and last-frame export',async()=>{
      for(const [startSeconds,endSeconds] of [[.125,.79],[.99,1]]){
        const job=await finish(await api('/api/exports','POST',{...request,startSeconds,endSeconds,upscale:{modelId:'nomos-weak',scale:2}}));
        assert.ok(Math.abs(job.result.duration-(endSeconds-startSeconds))<.1);
        await api(`/api/exports/${job.id}`,'DELETE');
      }
    });
    await t.test('rotated timeline preserves frame order and audio sync across full sequence',async()=>{
      const timelinePath=join(fixtures,'timeline.mp4'), rotatedPath=join(fixtures,'rotated.mp4');
      ffmpeg(['-f','lavfi','-i',"color=red:size=64x48:rate=24:duration=3,drawbox=color=lime:t=fill:enable='between(t,1,1.999)',drawbox=color=blue:t=fill:enable='gte(t,2)'",
        '-f','lavfi','-i','aevalsrc=if(lt(t\\,1)\\,sin(2*PI*440*t)\\,if(lt(t\\,2)\\,sin(2*PI*880*t)\\,sin(2*PI*1320*t))):s=48000:d=3',
        '-c:v','libx264','-g','72','-c:a','aac',timelinePath]);
      ffmpeg(['-display_rotation:v:0','90','-i',timelinePath,'-c','copy',rotatedPath]);
      const r=await fetch(`${url}/api/media/${crypto.randomUUID()}?name=rotated.mp4`,{method:'PUT',headers:{'X-VidCropper':'1','Content-Type':'application/octet-stream'},body:await readFile(rotatedPath)});
      assert.equal(r.status,200);const rotated=await r.json();
      const job=await finish(await api('/api/exports','POST',{...request,mediaId:rotated.id,sourceWidth:48,sourceHeight:64,
        crop:{x:1,y:3,width:45,height:59},startSeconds:.75,endSeconds:2.25,upscale:{modelId:'nomos-weak',scale:2}}));
      assert.equal(job.result.width,90);assert.equal(job.result.height,118);
      const path=join(fixtures,'timeline-result.mp4');
      await writeFile(path,Buffer.from(await (await fetch(`${url}/api/exports/${job.id}/download`)).arrayBuffer()));
      for(const offset of [.08,.5,1.35]){
        const second=Math.floor(.75+offset);
        const pixels=spawnSync('ffmpeg',['-v','error','-ss',String(offset),'-i',path,'-frames:v','1','-vf','scale=1:1','-pix_fmt','rgb24','-f','rawvideo','pipe:1']);
        assert.equal(pixels.status,0);assert.ok(pixels.stdout[second]>180,`${offset}: color ${second}: ${[...pixels.stdout]}`);
        const samples=spawnSync('ffmpeg',['-v','error','-ss',String(offset),'-i',path,'-t','0.05','-vn','-ac','1','-ar','48000','-f','f32le','pipe:1']);
        assert.equal(samples.status,0,samples.stderr.toString());let crossings=0;
        for(let i=4;i<samples.stdout.length;i+=4)if(samples.stdout.readFloatLE(i-4)<=0&&samples.stdout.readFloatLE(i)>0)crossings++;
        const frequency=crossings/(samples.stdout.length/4/48000);
        assert.ok(Math.abs(frequency-440*(second+1))<50,`audio ${offset}: ${frequency}`);
      }
      await api(`/api/exports/${job.id}`,'DELETE');await api(`/api/media/${rotated.id}`,'DELETE');
    });
    await t.test('invalid AI model and scale rejected before processing',async()=>{
      for(const upscale of [{modelId:'missing',scale:2},{modelId:'nomos-weak',scale:1}]){
        const r=await fetch(url+'/api/exports',{method:'POST',headers,body:JSON.stringify({...request,upscale})});
        assert.equal(r.status,400);
      }
    });
    await t.test('busy export rejects package changes, second export; cancellation cleans up',async()=>{
      const job=await api('/api/exports','POST',{...request,upscale:{modelId:'realesrgan',scale:4}});
      for(const path of ['/api/ai/models/nomos-weak/install','/api/ai/models/nomos-weak/rollback','/api/ai/models/nomos-weak/check','/api/exports']){
        const r=await fetch(url+path,{method:'POST',headers,body:path==='/api/exports'?JSON.stringify(request):undefined});assert.equal(r.status,409);
      }
      const stopped=await api(`/api/exports/${job.id}/cancel`,'POST');assert.equal(stopped.status,'cancelled');
      assert.equal((await fetch(`${url}/api/exports/${job.id}/download`)).status,409);
      await api(`/api/exports/${job.id}`,'DELETE');
    });
  } finally {
    if(media)await api(`/api/media/${media.id}`,'DELETE');
    if(server){await api('/api/shutdown','POST'); await new Promise(r=>server.once('exit',r));}
  }
});
