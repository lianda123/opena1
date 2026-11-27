const fs = require('node:fs');
const validator = require(process.argv[2]);
const path = require('node:path').join(__dirname, 'artifacts', 'portable-fixture.glb');
validator.validateBytes(new Uint8Array(fs.readFileSync(path)), {uri:'portable-fixture.glb',maxIssues:40}).then(report=>{
 console.log('Khronos GLB validation: '+report.issues.numErrors+' errors, '+report.issues.numWarnings+' warnings');
 for(const m of report.issues.messages) if(m.severity<=1) console.log(m.code+': '+m.message);
 if(report.issues.numErrors) process.exitCode=1;
}).catch(e=>{console.error(e);process.exitCode=1;});

