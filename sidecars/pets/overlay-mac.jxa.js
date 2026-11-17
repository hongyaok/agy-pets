ObjC.import('Cocoa');
ObjC.import('WebKit');
ObjC.import('stdlib');

function resolvePort(argv) {
  if (argv && argv.length > 0) {
    var p = parseInt(String(argv[0]), 10);
    if (p >= 1 && p <= 65535) return p;
  }
  var procArgs = $.NSProcessInfo.processInfo.arguments;
  var count = procArgs.count;
  for (var i = 0; i < count; i++) {
    var s = ObjC.unwrap(procArgs.objectAtIndex(i));
    var n = parseInt(s, 10);
    if (n >= 1 && n <= 65535 && String(n) === String(s).trim()) return n;
  }
  return 0;
}

function getPrimaryScreenHeight() {
  var screens = $.NSScreen.screens;
  if (screens && screens.count > 0) {
    return screens.objectAtIndex(0).frame.size.height;
  }
  return $.NSScreen.mainScreen ? $.NSScreen.mainScreen.frame.size.height : 900;
}

function run(argv) {
  var port = resolvePort(argv);
  if (!port) {
    $.exit(1);
  }

  var app = $.NSApplication.sharedApplication;
  app.setActivationPolicy($.NSApplicationActivationPolicyAccessory);
  app.finishLaunching;

  var screens = $.NSScreen.screens;
  var initialScreen = (screens && screens.count > 0) ? screens.objectAtIndex(0) : $.NSScreen.mainScreen;
  var initVf = initialScreen ? initialScreen.visibleFrame : $.NSMakeRect(0, 0, 1440, 820);
  var initW = 120;
  var initH = 124;
  var initX = initVf.origin.x + initVf.size.width - initW - 20;
  var initCocoaY = initVf.origin.y + 12;

  var win = $.NSWindow.alloc.initWithContentRectStyleMaskBackingDefer(
    $.NSMakeRect(initX, initCocoaY, initW, initH),
    $.NSWindowStyleMaskBorderless,
    $.NSBackingStoreBuffered,
    false
  );
  win.setOpaque(false);
  win.setBackgroundColor($.NSColor.clearColor);
  win.setHasShadow(false);
  win.setLevel($.NSFloatingWindowLevel);
  win.setCollectionBehavior(
    $.NSWindowCollectionBehaviorCanJoinAllSpaces |
    $.NSWindowCollectionBehaviorFullScreenAuxiliary
  );
  win.setIgnoresMouseEvents(false);
  win.setAcceptsMouseMovedEvents(true);
  win.setReleasedWhenClosed(false);

  var wkConfig = $.WKWebViewConfiguration.alloc.init;
  var webView = $.WKWebView.alloc.initWithFrameConfiguration(
    $.NSMakeRect(0, 0, initW, initH),
    wkConfig
  );
  webView.setValueForKey($(false), $('drawsBackground'));
  webView.setAutoresizingMask($.NSViewWidthSizable | $.NSViewHeightSizable);
  win.setContentView(webView);
  win.orderFrontRegardless;

  var targetUrl = $.NSURL.URLWithString($('http://127.0.0.1:' + port + '/overlay'));
  webView.loadRequest($.NSURLRequest.requestWithURL(targetUrl));

  var lastX = -1;
  var lastY = -1;
  var lastW = -1;
  var lastH = -1;
  var gotReady = false;
  var startedAt = Date.now();
  var distantPast = $.NSDate.distantPast;

  while (true) {
    var untilDate = $.NSDate.dateWithTimeIntervalSinceNow(0.016);
    var ev = app.nextEventMatchingMaskUntilDateInModeDequeue(
      $.NSEventMaskAny,
      untilDate,
      $.NSDefaultRunLoopMode,
      true
    );
    while (ev && !(typeof ev.isNil === 'function' && ev.isNil())) {
      app.sendEvent(ev);
      ev = app.nextEventMatchingMaskUntilDateInModeDequeue(
        $.NSEventMaskAny,
        distantPast,
        $.NSDefaultRunLoopMode,
        true
      );
    }
    app.updateWindows;

    var primaryH = getPrimaryScreenHeight();
    var activeScreen = win.screen || $.NSScreen.mainScreen ||
      ((screens && screens.count > 0) ? screens.objectAtIndex(0) : null);
    var vf = activeScreen ? activeScreen.visibleFrame : $.NSMakeRect(0, 0, 1440, 820);

    var screenLeft = vf.origin.x;
    var screenTop = primaryH - (vf.origin.y + vf.size.height);
    var screenW = vf.size.width;
    var screenH = vf.size.height;

    var mouse = $.NSEvent.mouseLocation;
    var cursorX = mouse.x;
    var cursorY = primaryH - mouse.y;
    var buttons = $.NSEvent.pressedMouseButtons;

    var js =
      'if (window.__updateMacHost) window.__updateMacHost(' +
      cursorX + ',' + cursorY + ',' + buttons + ',' +
      screenLeft + ',' + screenTop + ',' + screenW + ',' + screenH + ');';
    try {
      webView.evaluateJavaScriptCompletionHandler($(js), null);
    } catch (e) {
      // Ignore transient evaluation errors while WebView is initializing.
    }

    var rawTitle = '';
    try {
      rawTitle = ObjC.unwrap(webView.title) || '';
    } catch (e) {
      rawTitle = '';
    }

    if (rawTitle && rawTitle.charAt(0) === '{') {
      try {
        var st = JSON.parse(rawTitle);
        if (st.quit) {
          win.orderOut(null);
          $.exit(0);
        }
        if (st.ready && Number.isFinite(st.w) && Number.isFinite(st.h)) {
          gotReady = true;
          var nx = Math.round(st.x);
          var ny = Math.round(st.y);
          var nw = Math.max(40, Math.round(st.w));
          var nh = Math.max(40, Math.round(st.h));
          if (nx !== lastX || ny !== lastY || nw !== lastW || nh !== lastH) {
            lastX = nx;
            lastY = ny;
            lastW = nw;
            lastH = nh;
            var cocoaY = primaryH - (ny + nh);
            win.setFrameDisplay($.NSMakeRect(nx, cocoaY, nw, nh), true);
          }
        }
      } catch (e) {
        // Ignore malformed title updates.
      }
    }

    if (!gotReady && Date.now() - startedAt > 20000) {
      $.exit(1);
    }
  }
}
