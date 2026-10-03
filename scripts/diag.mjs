#!/usr/bin/env node
/** 短超时诊断：桥接是否还活着、最近做过什么操作、有没有卡住的。 */
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor } from '../src/launcher.js';

const info = readBridgeInfo(logPathFor('vanilla'));
if (!info) { console.log('日志里没有桥接信息'); process.exit(1); }
console.log(`连接 ${info.host}:${info.port}（每步 15 秒超时）`);

const client = new (await import('../src/gabp.js')).GabpClient({ ...info, requestTimeoutMs: 15_000 });
try {
  const welcome = await client.connect({ timeoutMs: 8000 });
  console.log(`握手 OK: ${welcome?.agentId}`);

  const st = await client.callTool('rimbridge/get_bridge_status');
  const s = st.state ?? {};
  console.log(`状态: programState=${s.programState} hasCurrentGame=${s.hasCurrentGame} paused=${s.paused} longEventPending=${s.longEventPending}`);
  console.log(`日志期刊: recentLogCount=${st.recentLogCount} latestLogSequence=${st.latestLogSequence}`);
  console.log(`操作期刊: recentOperationCount=${st.recentOperationCount} trackedOperationCount=${st.trackedOperationCount}`);
  console.log(`补丁: essentialApplied=${st.patches?.essentialApplied} optional失败=${st.patches?.optionalPatchFailureCount}`);

  console.log('\n最近的操作（找卡住的）:');
  const ops = await client.callTool('rimbridge/list_operations', { limit: 12 });
  const list = ops.operations ?? [];
  for (const o of list) {
    console.log(`  [${o.Status}] ${o.CapabilityId}  ${o.DurationMs}ms  ${o.Success ? 'OK' : (o.Error?.Message ?? '')}`.slice(0, 170));
  }

  console.log('\n最近的事件:');
  const ev = await client.callTool('rimbridge/list_operation_events', { limit: 10 });
  for (const e of (ev.events ?? []).slice(0, 10)) {
    console.log(`  ${e.EventType ?? e.eventType ?? '?'} ${e.OperationId ?? ''} ${e.CapabilityId ?? ''}`.slice(0, 160));
  }

  console.log('\n最近的日志:');
  const lg = await client.callTool('rimbridge/list_logs', { minimumLevel: 'warning', limit: 20 });
  for (const e of (lg.logs ?? []).slice(0, 12)) {
    console.log(`  [${e.level}] ${String(e.message).replace(/\s+/g, ' ').slice(0, 150)}`);
  }
} catch (err) {
  console.log(`诊断失败: ${err.message}`);
} finally {
  client.close();
}
