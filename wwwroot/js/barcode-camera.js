(() => {
    let activeStream = null;
    let scanTimer = null;
    let detector = null;
    let scanBusy = false;
    let lastCode = "";

    async function start(dotNetRef, videoId) {
        await stop(videoId);

        if (!("mediaDevices" in navigator) || !navigator.mediaDevices.getUserMedia) {
            await dotNetRef.invokeMethodAsync("OnCameraBarcodeError", "Camera access is not available in this browser.");
            return;
        }

        if (!("BarcodeDetector" in window)) {
            await dotNetRef.invokeMethodAsync("OnCameraBarcodeError", "BarcodeDetector is not supported in this browser.");
            return;
        }

        const video = document.getElementById(videoId);
        if (!video) {
            await dotNetRef.invokeMethodAsync("OnCameraBarcodeError", "Scanner video element was not found.");
            return;
        }

        activeStream = await navigator.mediaDevices.getUserMedia({
            video: {
                facingMode: { ideal: "environment" },
                width: { ideal: 1280 },
                height: { ideal: 720 }
            },
            audio: false
        });

        video.srcObject = activeStream;
        await video.play();

        try {
            detector = new BarcodeDetector({
                formats: ["ean_13", "upc_a", "ean_8", "upc_e"]
            });
        } catch {
            detector = new BarcodeDetector();
        }

        scanTimer = window.setInterval(async () => {
            if (scanBusy || !detector || !video || video.readyState < 2) {
                return;
            }

            scanBusy = true;
            try {
                const barcodes = await detector.detect(video);
                if (!barcodes || barcodes.length === 0) {
                    return;
                }

                const best = barcodes[0];
                const rawValue = (best.rawValue || "").trim();
                if (!rawValue || rawValue === lastCode) {
                    return;
                }

                lastCode = rawValue;
                await dotNetRef.invokeMethodAsync(
                    "OnCameraBarcodeDetected",
                    rawValue,
                    best.format || ""
                );
            } catch {
                // Ignore per-frame scanning failures and continue scanning.
            } finally {
                scanBusy = false;
            }
        }, 220);
    }

    async function stop(videoId) {
        if (scanTimer) {
            window.clearInterval(scanTimer);
            scanTimer = null;
        }
        detector = null;
        scanBusy = false;
        lastCode = "";

        if (activeStream) {
            for (const track of activeStream.getTracks()) {
                track.stop();
            }
            activeStream = null;
        }

        const video = document.getElementById(videoId);
        if (video) {
            video.pause();
            video.srcObject = null;
        }
    }

    window.catalogPilotBarcodeScanner = {
        start,
        stop
    };
})();
