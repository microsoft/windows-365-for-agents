// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.W365APlaygroundAgent.Screenshare;

/// <summary>
/// Static viewer HTML served by <c>GET /screenshare</c> as a top-level browser page (the agentic
/// Teams surface can't host task-module dialogs, so the card's Action.OpenUrl opens this page). The
/// opener is authenticated by an interactive Entra sign-in enforced server-side before this HTML is
/// served, so the page carries NO secrets: it reads the opaque ticket from the URL, redeems via POST
/// /api/screenshare/session (the same-origin auth cookie is sent automatically), dynamically loads
/// the SDK from the server-provided viewerUrl (script tag — no CORS), connects the ScreenShareViewer,
/// and syncs liveness/teardown via POST /api/screenshare/state.
/// </summary>
internal static class ScreenshareViewerPage
{
    public const string Html =
        """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <title>Cloud PC — Live View</title>
        <style>
          html,body{margin:0;height:100%;font-family:'Segoe UI',system-ui,sans-serif;background:#1b1a19;color:#f3f2f1;overflow:hidden;}
          #bar{height:44px;display:flex;align-items:center;gap:8px;padding:0 12px;background:#252423;border-bottom:1px solid #3b3a39;box-sizing:border-box;}
          #status{font-size:13px;display:inline-flex;align-items:center;}
          .dot{width:10px;height:10px;border-radius:50%;background:#8a8886;display:inline-block;margin-right:6px;}
          .dot.green{background:#6bb700;} .dot.blue{background:#0078d4;} .dot.red{background:#d13438;}
          button{background:#0078d4;color:#fff;border:0;border-radius:4px;padding:6px 12px;font:inherit;cursor:pointer;}
          button:disabled{opacity:.4;cursor:not-allowed;}
          button.danger{background:#a4262c;}
          #stage{position:absolute;top:44px;bottom:0;left:0;right:0;background:#000;}
          #panel{position:absolute;top:44px;left:0;right:0;bottom:0;display:none;align-items:center;justify-content:center;flex-direction:column;gap:14px;text-align:center;padding:24px;box-sizing:border-box;}
          #panelText{font-size:15px;max-width:420px;}
        </style>
        </head>
        <body>
          <div id="bar">
            <span id="status"><span class="dot" id="dot"></span><span id="statusText">starting…</span></span>
            <span style="flex:1"></span>
            <button id="takeBtn" disabled>Take control</button>
            <button id="releaseBtn" disabled>Release</button>
            <button id="stopBtn" class="danger" disabled>Stop</button>
          </div>
          <div id="stage"></div>
          <div id="panel"><div id="panelText"></div><button id="panelBtn" style="display:none"></button></div>

          <script>
          (function () {
            const $ = id => document.getElementById(id);
            const ticket = new URLSearchParams(location.search).get('ticket');
            let viewer = null, lastStatus = 'connecting', heartbeat = null, ended = false;

            function setStatus(text, cls) { $('statusText').textContent = text; $('dot').className = 'dot ' + (cls || ''); }
            function setControls(state) {
              $('takeBtn').disabled = !(state === 'connected' || state === 'view-only');
              $('releaseBtn').disabled = state !== 'controlling';
              $('stopBtn').disabled = (state === 'idle');
            }
            function showPanel(text, btnText, btnFn) {
              if (heartbeat) { clearInterval(heartbeat); heartbeat = null; }
              setStatus('closed', '');   // reset the top-bar (no longer stuck on "authorizing…"); neutral dot
              setControls('idle');       // the view isn't live — disable take/release/stop
              $('stage').style.display = 'none'; $('panel').style.display = 'flex';
              $('panelText').textContent = text;
              const b = $('panelBtn');
              if (btnText) { b.style.display = 'inline-block'; b.textContent = btnText; b.onclick = btnFn || null; }
              else b.style.display = 'none';
            }
            function endView(text) {
              if (ended) return; ended = true;
              try { viewer && viewer.stop(); } catch (e) {}
              setControls('idle'); showPanel(text);
            }

            function authHeaders() { return { 'Content-Type': 'application/json' }; } // same-origin cookie is sent automatically

            async function reportState(status, useBeacon, reason) {
              if (!ticket) return;
              const payload = { ticket, sdkStatus: status, visibility: document.visibilityState, reason: reason || null };
              if (useBeacon && navigator.sendBeacon) { // tab close — beacon still sends the same-origin cookie
                navigator.sendBeacon('/api/screenshare/state', new Blob([JSON.stringify(payload)], { type: 'application/json' }));
                return;
              }
              try {
                const resp = await fetch('/api/screenshare/state', { method: 'POST', headers: authHeaders(), body: JSON.stringify(payload) });
                if (resp.ok) { const j = await resp.json(); if (j.directive === 'revoked' || j.directive === 'ended') endView('This live view has ended.'); }
              } catch (e) {}
            }

            function loadSdk(viewerUrl) {
              return new Promise((resolve, reject) => {
                const s = document.createElement('script');
                s.src = viewerUrl.replace(/\/+$/, '') + '/screenshare-embed.js';
                s.onload = resolve; s.onerror = () => reject(new Error('SDK load failed'));
                document.head.appendChild(s);
              });
            }

            function onStatus(state) {
              lastStatus = state;
              const map = { connecting: ['connecting…', 'blue'], connected: ['Live — viewing', 'green'], controlling: ['Live — you have control', 'green'], 'view-only': ['Live — viewing', 'green'], disconnected: ['Disconnected', 'red'] };
              const m = map[state] || [state, ''];
              setStatus(m[0], m[1]); setControls(state);
              reportState(state, false, state === 'disconnected' ? 'sdk-disconnected' : undefined);
              if (state === 'disconnected') endView('The Cloud PC session ended.');
            }
            function onError(code, msg) {
              if (code === 'TOKEN_EXPIRED') { endView('The live view expired. Go back to Teams and ask the agent to share again.'); return; }
              if (code === 'START_FAILED') { showPanel('Could not connect to the Cloud PC.', 'Try again', function () { location.reload(); }); return; }
              setStatus('error: ' + code, 'red');
            }

            async function start() {
              if (!ticket) { showPanel('Missing ticket.'); return; }

              setStatus('authorizing…', 'blue');
              let session;
              try {
                const resp = await fetch('/api/screenshare/session', { method: 'POST', headers: authHeaders(), body: JSON.stringify({ ticket }) });
                if (resp.status === 401) { showPanel('Your sign-in has expired.', 'Sign in again', function () { location.reload(); }); return; }
                if (resp.status === 403) { showPanel('You are not authorized to view this session.'); return; }
                if (resp.status === 410) { showPanel('This live link has expired. Go back to Teams and ask the agent to share again.'); return; }
                if (!resp.ok) { showPanel('Could not start the live view.'); return; }
                session = await resp.json();
              } catch (e) { showPanel('Could not reach the service.'); return; }

              try { await loadSdk(session.viewerUrl); } catch (e) { showPanel('The viewer failed to load.'); return; }

              try {
                viewer = new ScreenShareViewer({
                  container: $('stage'), computerUrl: session.computerUrl, viewerUrl: session.viewerUrl,
                  mode: session.mode === 'viewonly' ? 'viewOnly' : 'interactive'
                });
                viewer.on('statusChanged', onStatus);
                viewer.on('error', onError);
                setStatus('connecting…', 'blue');
                await viewer.connect(session.ariToken);
              } catch (e) { onError('START_FAILED', e && e.message); return; }

              heartbeat = setInterval(() => reportState(lastStatus, false), 12000);
              window.addEventListener('pagehide', () => reportState('disconnected', true, 'page-hide'));
            }

            $('takeBtn').onclick = () => { try { viewer.takeControl(); } catch (e) {} };
            $('releaseBtn').onclick = () => { try { viewer.releaseControl(); } catch (e) {} };
            $('stopBtn').onclick = () => { reportState('disconnected', false, 'user-stop'); endView('You ended the live view.'); };
            document.addEventListener('keydown', e => { if (e.key === 'Escape' && !$('releaseBtn').disabled) $('releaseBtn').click(); });

            start();
          })();
          </script>
        </body>
        </html>
        """;
}
