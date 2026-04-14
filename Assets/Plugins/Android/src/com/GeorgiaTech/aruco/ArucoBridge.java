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

    static {
        System.loadLibrary("opencv_java4");
    }

    // Keep the detector as a static member to avoid re-initializing it every frame
    private static ArucoDetector detector = null;

    public static int detectMarkers(byte[] imageData, int width, int height) {
        Mat gray = null;
        Mat ids = null;
        List<Mat> corners = new ArrayList<>();

        try {
            initializeDetector();

            gray = new Mat(height, width, CvType.CV_8UC1);
            gray.put(0, 0, imageData);

            ids = new Mat();
            detector.detectMarkers(gray, corners, ids);

            return (int) ids.total();
        } catch (Exception e) {
            Log.e(TAG, "detectMarkers failed", e);
            return -1;
        } finally {
            for (Mat corner : corners) {
                corner.release();
            }

            if (gray != null) {
                gray.release();
            }

            if (ids != null) {
                ids.release();
            }
        }
    }

    public static float[] detectMarkersDetailed(byte[] imageData, int width, int height) {
        Mat gray = null;
        Mat ids = null;
        List<Mat> corners = new ArrayList<>();

        try {
            initializeDetector();

            gray = new Mat(height, width, CvType.CV_8UC1);
            gray.put(0, 0, imageData);

            ids = new Mat();
            detector.detectMarkers(gray, corners, ids);

            int count = (int) ids.total();
            float[] flattened = new float[1 + (count * 9)];
            flattened[0] = count;
            if (count == 0) {
                return flattened;
            }

            int[] idValues = new int[count];
            ids.get(0, 0, idValues);

            for (int markerIndex = 0; markerIndex < count; markerIndex++) {
                int baseIndex = 1 + (markerIndex * 9);
                flattened[baseIndex] = idValues[markerIndex];

                Mat cornerMat = corners.get(markerIndex);
                float[] cornerData = new float[(int) (cornerMat.total() * cornerMat.channels())];
                cornerMat.get(0, 0, cornerData);

                for (int cornerIndex = 0; cornerIndex < 4; cornerIndex++) {
                    int cornerBaseIndex = baseIndex + 1 + (cornerIndex * 2);
                    int sourceBaseIndex = cornerIndex * 2;
                    flattened[cornerBaseIndex] = cornerData[sourceBaseIndex];
                    flattened[cornerBaseIndex + 1] = cornerData[sourceBaseIndex + 1];
                }
            }

            return flattened;

        } catch (Exception e) {
            Log.e(TAG, "detectMarkersDetailed failed", e);
            return new float[] { -1f };
        } finally {
            for (Mat corner : corners) {
                corner.release();
            }

            if (gray != null) {
                gray.release();
            }

            if (ids != null) {
                ids.release();
            }
        }
    }

    public static float[] estimateMarkerPoses(byte[] imageData, int width, int height, float fx, float fy, float cx, float cy, float markerLengthMeters) {
        Mat gray = null;
        Mat ids = null;
        Mat cameraMatrix = null;
        MatOfDouble distCoeffs = null;
        List<Mat> corners = new ArrayList<>();

        try {
            initializeDetector();

            gray = new Mat(height, width, CvType.CV_8UC1);
            gray.put(0, 0, imageData);

            ids = new Mat();
            detector.detectMarkers(gray, corners, ids);

            int count = (int) ids.total();
            float[] flattened = new float[1 + (count * 7)];
            if (count == 0) {
                flattened[0] = 0f;
                return flattened;
            }

            int[] idValues = new int[count];
            ids.get(0, 0, idValues);

            cameraMatrix = Mat.eye(3, 3, CvType.CV_64F);
            cameraMatrix.put(0, 0, fx);
            cameraMatrix.put(1, 1, fy);
            cameraMatrix.put(0, 2, cx);
            cameraMatrix.put(1, 2, cy);

            distCoeffs = new MatOfDouble(0.0, 0.0, 0.0, 0.0, 0.0);

            double halfSize = markerLengthMeters * 0.5;
            MatOfPoint3f objectPoints = new MatOfPoint3f(
                new Point3(-halfSize, halfSize, 0.0),
                new Point3(halfSize, halfSize, 0.0),
                new Point3(halfSize, -halfSize, 0.0),
                new Point3(-halfSize, -halfSize, 0.0)
            );

            int solvedCount = 0;
            for (int markerIndex = 0; markerIndex < count; markerIndex++) {
                if (!shouldTrackMarkerId(idValues[markerIndex])) {
                    continue;
                }

                Mat cornerMat = corners.get(markerIndex);
                float[] cornerData = new float[(int) (cornerMat.total() * cornerMat.channels())];
                cornerMat.get(0, 0, cornerData);

                MatOfPoint2f imagePoints = new MatOfPoint2f(
                    new Point(cornerData[0], cornerData[1]),
                    new Point(cornerData[2], cornerData[3]),
                    new Point(cornerData[4], cornerData[5]),
                    new Point(cornerData[6], cornerData[7])
                );

                Mat rvec = new Mat();
                Mat tvec = new Mat();

                try {
                    boolean solved = Calib3d.solvePnP(
                        objectPoints,
                        imagePoints,
                        cameraMatrix,
                        distCoeffs,
                        rvec,
                        tvec,
                        false,
                        Calib3d.SOLVEPNP_IPPE_SQUARE);

                    if (!solved) {
                        solved = Calib3d.solvePnP(objectPoints, imagePoints, cameraMatrix, distCoeffs, rvec, tvec);
                    }

                    if (solved) {
                        int baseIndex = 1 + (solvedCount * 7);
                        double[] rvecData = new double[3];
                        double[] tvecData = new double[3];
                        rvec.get(0, 0, rvecData);
                        tvec.get(0, 0, tvecData);

                        flattened[baseIndex] = idValues[markerIndex];
                        flattened[baseIndex + 1] = (float) rvecData[0];
                        flattened[baseIndex + 2] = (float) rvecData[1];
                        flattened[baseIndex + 3] = (float) rvecData[2];
                        flattened[baseIndex + 4] = (float) tvecData[0];
                        flattened[baseIndex + 5] = (float) tvecData[1];
                        flattened[baseIndex + 6] = (float) tvecData[2];
                        solvedCount++;
                    }
                } finally {
                    imagePoints.release();
                    rvec.release();
                    tvec.release();
                }
            }

            objectPoints.release();
            flattened[0] = solvedCount;
            return Arrays.copyOf(flattened, 1 + (solvedCount * 7));
        } catch (Exception e) {
            Log.e(TAG, "estimateMarkerPoses failed", e);
            return new float[] { -1f };
        } finally {
            for (Mat corner : corners) {
                corner.release();
            }

            if (gray != null) {
                gray.release();
            }

            if (ids != null) {
                ids.release();
            }

            if (cameraMatrix != null) {
                cameraMatrix.release();
            }

            if (distCoeffs != null) {
                distCoeffs.release();
            }
        }
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
