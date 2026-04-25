package com.GeorgiaTech.aruco;

import android.util.Log;
import org.opencv.calib3d.Calib3d;
import org.opencv.core.*;
import org.opencv.objdetect.*;
import java.util.Arrays;
import java.util.ArrayList;
import java.util.List;

public class ArucoBridge {
    private static final String TAG = "ArucoBridge";
    private static final int[] TRACKED_MARKER_IDS = new int[] { 0, 1, 2, 3, 4 };
    private static final Mat CAMERA_MATRIX = Mat.eye(3, 3, CvType.CV_64F);
    private static final MatOfDouble DIST_COEFFS = new MatOfDouble(0.0, 0.0, 0.0, 0.0, 0.0);
    private static final Mat GRAY = new Mat();
    private static final Mat IDS = new Mat();
    private static final List<Mat> CORNERS = new ArrayList<>();
    private static final MatOfPoint2f IMAGE_POINTS = new MatOfPoint2f();
    private static final Mat RVEC = new Mat();
    private static final Mat TVEC = new Mat();
    private static final float[] CORNER_DATA = new float[8];
    private static final double[] RVEC_DATA = new double[3];
    private static final double[] TVEC_DATA = new double[3];
    private static MatOfPoint3f objectPoints = null;
    private static float cachedMarkerLengthMeters = -1f;

    static {
        System.loadLibrary("opencv_java4");
    }

    // Keep the detector as a static member to avoid re-initializing it every frame
    private static ArucoDetector detector = null;

    public static synchronized int detectMarkers(byte[] imageData, int width, int height) {
        try {
            initializeDetector();

            prepareDetectionInputs(imageData, width, height);
            detector.detectMarkers(GRAY, CORNERS, IDS);

            return (int) IDS.total();
        } catch (Exception e) {
            Log.e(TAG, "detectMarkers failed", e);
            return -1;
        } finally {
            releaseCorners();
        }
    }

    public static synchronized float[] detectMarkersDetailed(byte[] imageData, int width, int height) {
        try {
            initializeDetector();

            prepareDetectionInputs(imageData, width, height);
            detector.detectMarkers(GRAY, CORNERS, IDS);

            int count = (int) IDS.total();
            float[] flattened = new float[1 + (count * 9)];
            flattened[0] = count;
            if (count == 0) {
                return flattened;
            }

            int[] idValues = new int[count];
            IDS.get(0, 0, idValues);

            for (int markerIndex = 0; markerIndex < count; markerIndex++) {
                int baseIndex = 1 + (markerIndex * 9);
                flattened[baseIndex] = idValues[markerIndex];

                Mat cornerMat = CORNERS.get(markerIndex);
                cornerMat.get(0, 0, CORNER_DATA);

                for (int cornerIndex = 0; cornerIndex < 4; cornerIndex++) {
                    int cornerBaseIndex = baseIndex + 1 + (cornerIndex * 2);
                    int sourceBaseIndex = cornerIndex * 2;
                    flattened[cornerBaseIndex] = CORNER_DATA[sourceBaseIndex];
                    flattened[cornerBaseIndex + 1] = CORNER_DATA[sourceBaseIndex + 1];
                }
            }

            return flattened;

        } catch (Exception e) {
            Log.e(TAG, "detectMarkersDetailed failed", e);
            return new float[] { -1f };
        } finally {
            releaseCorners();
        }
    }

    public static synchronized float[] estimateMarkerPoses(byte[] imageData, int width, int height, float fx, float fy, float cx, float cy, float markerLengthMeters) {
        try {
            initializeDetector();
            ensurePoseEstimationInputs(fx, fy, cx, cy, markerLengthMeters);

            prepareDetectionInputs(imageData, width, height);
            detector.detectMarkers(GRAY, CORNERS, IDS);

            int count = (int) IDS.total();
            float[] flattened = new float[1 + (count * 7)];
            if (count == 0) {
                flattened[0] = 0f;
                return flattened;
            }

            int[] idValues = new int[count];
            IDS.get(0, 0, idValues);

            int solvedCount = 0;
            for (int markerIndex = 0; markerIndex < count; markerIndex++) {
                if (!shouldTrackMarkerId(idValues[markerIndex])) {
                    continue;
                }

                Mat cornerMat = CORNERS.get(markerIndex);
                cornerMat.get(0, 0, CORNER_DATA);

                IMAGE_POINTS.fromArray(
                    new Point(CORNER_DATA[0], CORNER_DATA[1]),
                    new Point(CORNER_DATA[2], CORNER_DATA[3]),
                    new Point(CORNER_DATA[4], CORNER_DATA[5]),
                    new Point(CORNER_DATA[6], CORNER_DATA[7])
                );

                RVEC.create(3, 1, CvType.CV_64F);
                TVEC.create(3, 1, CvType.CV_64F);

                boolean solved = Calib3d.solvePnP(
                    objectPoints,
                    IMAGE_POINTS,
                    CAMERA_MATRIX,
                    DIST_COEFFS,
                    RVEC,
                    TVEC,
                    false,
                    Calib3d.SOLVEPNP_IPPE_SQUARE);

                if (!solved) {
                    solved = Calib3d.solvePnP(objectPoints, IMAGE_POINTS, CAMERA_MATRIX, DIST_COEFFS, RVEC, TVEC);
                }

                if (solved) {
                    int baseIndex = 1 + (solvedCount * 7);
                    RVEC.get(0, 0, RVEC_DATA);
                    TVEC.get(0, 0, TVEC_DATA);

                    flattened[baseIndex] = idValues[markerIndex];
                    flattened[baseIndex + 1] = (float) RVEC_DATA[0];
                    flattened[baseIndex + 2] = (float) RVEC_DATA[1];
                    flattened[baseIndex + 3] = (float) RVEC_DATA[2];
                    flattened[baseIndex + 4] = (float) TVEC_DATA[0];
                    flattened[baseIndex + 5] = (float) TVEC_DATA[1];
                    flattened[baseIndex + 6] = (float) TVEC_DATA[2];
                    solvedCount++;
                }
            }

            flattened[0] = solvedCount;
            return Arrays.copyOf(flattened, 1 + (solvedCount * 7));
        } catch (Exception e) {
            Log.e(TAG, "estimateMarkerPoses failed", e);
            return new float[] { -1f };
        } finally {
            releaseCorners();
        }
    }

    private static void prepareDetectionInputs(byte[] imageData, int width, int height) {
        releaseCorners();
        GRAY.create(height, width, CvType.CV_8UC1);
        GRAY.put(0, 0, imageData);
    }

    private static void releaseCorners() {
        for (Mat corner : CORNERS) {
            corner.release();
        }

        CORNERS.clear();
    }

    private static void ensurePoseEstimationInputs(float fx, float fy, float cx, float cy, float markerLengthMeters) {
        CAMERA_MATRIX.put(0, 0, fx);
        CAMERA_MATRIX.put(0, 1, 0.0);
        CAMERA_MATRIX.put(0, 2, cx);
        CAMERA_MATRIX.put(1, 0, 0.0);
        CAMERA_MATRIX.put(1, 1, fy);
        CAMERA_MATRIX.put(1, 2, cy);
        CAMERA_MATRIX.put(2, 0, 0.0);
        CAMERA_MATRIX.put(2, 1, 0.0);
        CAMERA_MATRIX.put(2, 2, 1.0);

        if (objectPoints != null && Math.abs(cachedMarkerLengthMeters - markerLengthMeters) < 1e-6f) {
            return;
        }

        if (objectPoints != null) {
            objectPoints.release();
        }

        double halfSize = markerLengthMeters * 0.5;
        objectPoints = new MatOfPoint3f(
            new Point3(-halfSize, halfSize, 0.0),
            new Point3(halfSize, halfSize, 0.0),
            new Point3(halfSize, -halfSize, 0.0),
            new Point3(-halfSize, -halfSize, 0.0)
        );
        cachedMarkerLengthMeters = markerLengthMeters;
    }

    private static boolean shouldTrackMarkerId(int markerId) {
        for (int allowedId : TRACKED_MARKER_IDS) {
            if (allowedId == markerId) {
                return true;
            }
        }

        return false;
    }

    private static void initializeDetector() {
        if (detector != null) {
            return;
        }

        Dictionary dict = Objdetect.getPredefinedDictionary(Objdetect.DICT_4X4_50);
        DetectorParameters params = new DetectorParameters();

        // Optimized params for Quest 3 Passthrough
        params.set_minMarkerPerimeterRate(0.03);
        params.set_maxMarkerPerimeterRate(1.0);
        params.set_adaptiveThreshWinSizeMin(5);
        params.set_adaptiveThreshWinSizeMax(15);
        params.set_adaptiveThreshWinSizeStep(5);
        params.set_cornerRefinementMethod(Objdetect.CORNER_REFINE_SUBPIX);
        params.set_cornerRefinementWinSize(5);
        params.set_cornerRefinementMaxIterations(20);
        params.set_cornerRefinementMinAccuracy(0.05);

        detector = new ArucoDetector(dict, params);
    }
}
