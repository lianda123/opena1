const fs = require('node:fs');
const validator = require(process.argv[2]);
const path = require('node:path');
(async()=>{
 for(const name of ['portable-fixture.glb','portable-mixed-fixture.glb','portable-untextured-fixture.glb']) {
  const file=path.join(__dirname,'artifacts',name);
  const report=await validator.validateBytes(new Uint8Array(fs.readFileSync(file)), {uri:name,maxIssues:40});
  console.log('Khronos GLB validation ('+name+'): '+report.issues.numErrors+' errors, '+report.issues.numWarnings+' warnings');
  for(const m of report.issues.messages) if(m.severity<=1) console.log(m.code+': '+m.message);
  if(report.issues.numErrors||report.issues.numWarnings) process.exitCode=1;
 }
})().catch(e=>{console.error(e);process.exitCode=1;});
