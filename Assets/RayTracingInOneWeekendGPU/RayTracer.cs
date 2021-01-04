﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace RayTracingInOneWeekendGPU
{
    public class RayTracer : MonoBehaviour
    {
        [SerializeField]
        private RenderTexture rtDefaultSettings;

        [SerializeField]
        private ComputeShader RayTraceKernels;

        [SerializeField]
        private Material FullScreenResolve;

        [SerializeField, Range(0.001f, 100f)]
        private float FocusDistance;

        // If set, FocusDistance is ignored and instead the FocusDistance is based on the FocusObject.
        [SerializeField]
        private Transform FocusObject;

        [SerializeField, Range(0.0001f, 2.5f)]
        private float Aperture;

        [SerializeField, Range(1, 64)]
        private int MaxBounces = 1;


        public static RayTracer Instance { get; private set; }



        private ComputeBuffer m_raysBuffer;
        private ComputeBuffer m_spheresBuffer;
        private ComputeBuffer m_fibSamples;




        /// <summary>
        /// Dirty flag for scene sync
        /// </summary>
        private bool m_sceneChanged = false;

        // The maximum number of bounces before terminating a ray.
        private int m_maxBounces = 1;

        private float m_lastAperture = -1;
        private float m_lastFocusDistance = -1;
        private Matrix4x4 m_lastCam;
        private Matrix4x4 m_lastProj;

        private int m_initCameraRaysKernel;
        private int m_rayTraceKernel;
        private int m_normalizeSamplesKernel;


        private Sphere[] m_sphereData;


        private System.Random m_rng = new System.Random();





        /// <summary>
        /// The number of super sampled rays to schedule
        /// </summary>
        private int m_superSamplingFactor = 8;

        /// <summary>
        /// The number of bounces per pixel to schedule.
        /// This number will be multiplied by m_superSamplingFactor on Dispatch.
        /// </summary>
        private int m_bouncesPerPixel = 8;


        [Header("Debug value")]
        public MeshFilter SphericalFibDebugMesh;

        public RenderTexture m_accumulatedImage;

        /// <summary>
        /// Sample Count is only public for debugging.
        /// In non-sample code this should be a read-only label.
        /// </summary>
        public int m_sampleCount;



        private void Awake()
        {
            Instance = this;
        }

        private void OnEnable()
        {
            // Force an update.
            m_lastAperture = -1;

            m_sampleCount = 0;

            // Make sure we start from a clean slate.
            // Under normal circumstances, this does nothing.
            ReclaimResources();

            //
            m_accumulatedImage = RenderTexture.GetTemporary(rtDefaultSettings.descriptor);
            RenderTexture.active = m_accumulatedImage;
            GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));
            RenderTexture.active = null;

            // Local constants make the next lines signfinicantly more readable.
            const int kBytesPerFloat = sizeof(float);
            const int kFloatsPerRay = 13;
            int numPixels = m_accumulatedImage.width * m_accumulatedImage.height;
            int numRays = numPixels * m_superSamplingFactor;

            // IMPORTANT NOTE: the byte size below must match the shader, not C#! In this case they match.
            m_raysBuffer = new ComputeBuffer(numRays,
                                             kBytesPerFloat * kFloatsPerRay + sizeof(int) + sizeof(int),
                                             ComputeBufferType.Counter);

            var samples = new Vector3[4096];
            SphericalFib(ref samples);
            m_fibSamples = new ComputeBuffer(samples.Length, 3 * kBytesPerFloat);
            m_fibSamples.SetData(samples);

            // Populate the scene.
            NotifySceneChanged();

            // Setup the RayTrace kernel.
            m_rayTraceKernel = RayTraceKernels.FindKernel("RayTrace");
            RayTraceKernels.SetTexture(m_rayTraceKernel, "_AccumulatedImage", m_accumulatedImage);
            RayTraceKernels.SetBuffer(m_rayTraceKernel, "_Spheres", m_spheresBuffer);
            RayTraceKernels.SetBuffer(m_rayTraceKernel, "_Rays", m_raysBuffer);
            RayTraceKernels.SetBuffer(m_rayTraceKernel, "_HemisphereSamples", m_fibSamples);

            // Setup the InitCameraRays kernel.
            m_initCameraRaysKernel = RayTraceKernels.FindKernel("InitCameraRays");
            RayTraceKernels.SetBuffer(m_initCameraRaysKernel, "_Rays", m_raysBuffer);
            RayTraceKernels.SetBuffer(m_initCameraRaysKernel, "_Spheres", m_spheresBuffer);
            RayTraceKernels.SetTexture(m_initCameraRaysKernel, "_AccumulatedImage", m_accumulatedImage);
            RayTraceKernels.SetBuffer(m_initCameraRaysKernel, "_HemisphereSamples", m_fibSamples);

            // Setup the NormalizeSamples kernel.
            m_normalizeSamplesKernel = RayTraceKernels.FindKernel("NormalizeSamples");
            RayTraceKernels.SetBuffer(m_normalizeSamplesKernel, "_Rays", m_raysBuffer);
            RayTraceKernels.SetBuffer(m_normalizeSamplesKernel, "_Spheres", m_spheresBuffer);
            RayTraceKernels.SetTexture(m_normalizeSamplesKernel, "_AccumulatedImage", m_accumulatedImage);
            RayTraceKernels.SetBuffer(m_normalizeSamplesKernel, "_HemisphereSamples", m_fibSamples);

            //
            RayTraceKernels.SetInt("_ImageWidth", m_accumulatedImage.width);
            RayTraceKernels.SetInt("_ImageHeight", m_accumulatedImage.height);

            // DOF parameter defaults.
            RayTraceKernels.SetFloat("_Aperture", 2.0f);
            RayTraceKernels.SetFloat("_FocusDistance", 5.0f);

            // Assign the texture to the main materail, to blit to screen.
            FullScreenResolve.mainTexture = m_accumulatedImage;

        }

        private void OnDisable()
        {
            ReclaimResources();
        }

        void SetMatrix(ComputeShader shader, string name, Matrix4x4 matrix)
        {
            float[] matrixFloats = new float[] {
                matrix[0,0], matrix[1, 0], matrix[2, 0], matrix[3, 0],
                matrix[0,1], matrix[1, 1], matrix[2, 1], matrix[3, 1],
                matrix[0,2], matrix[1, 2], matrix[2, 2], matrix[3, 2],
                matrix[0,3], matrix[1, 3], matrix[2, 3], matrix[3, 3]
            };
            shader.SetFloats(name, matrixFloats);
        }

        void OnRenderObject()
        {
            if (FocusObject != null)
            {
                FocusDistance = (transform.position - FocusObject.position).magnitude;
                transform.LookAt(FocusObject.position);
            }

            var camInverse = Camera.main.cameraToWorldMatrix;
            var ProjInverse = Camera.main.projectionMatrix.inverse;

            RayTraceKernels.SetFloat("_Seed01", (float)m_rng.NextDouble());
            RayTraceKernels.SetFloat("_R0", (float)m_rng.NextDouble());
            RayTraceKernels.SetFloat("_R1", (float)m_rng.NextDouble());
            RayTraceKernels.SetFloat("_R2", (float)m_rng.NextDouble());

            if (m_sceneChanged || m_lastAperture != Aperture || m_lastFocusDistance != FocusDistance || m_maxBounces != MaxBounces || camInverse != m_lastCam || ProjInverse != m_lastProj)
            {
                SetMatrix(RayTraceKernels, "_Camera", Camera.main.worldToCameraMatrix);
                SetMatrix(RayTraceKernels, "_CameraI", Camera.main.cameraToWorldMatrix);
                SetMatrix(RayTraceKernels, "_ProjectionI", ProjInverse);
                SetMatrix(RayTraceKernels, "_Projection", Camera.main.projectionMatrix);
                RayTraceKernels.SetFloat("_FocusDistance", FocusDistance);
                RayTraceKernels.SetFloat("_Aperture", Aperture);

                RayTraceKernels.SetInt("_SphereCount", m_spheresBuffer.count);
                RayTraceKernels.SetInt("_RayCount", m_raysBuffer.count);


                m_sceneChanged = false;
                m_lastAperture = Aperture;
                m_lastFocusDistance = FocusDistance;
                m_lastCam = camInverse;
                m_lastProj = ProjInverse;
                //var rayStart = new Vector3(0, 0, 0);
                //var rayEnd = new Vector3(0, 0, 1);
                //rayStart = Camera.main.projectionMatrix.inverse.MultiplyPoint(rayStart);
                //rayEnd = Camera.main.projectionMatrix.inverse.MultiplyPoint(rayEnd);

                m_maxBounces = MaxBounces;
                m_sampleCount = 0;

                RayTraceKernels.Dispatch(m_normalizeSamplesKernel,
                                         m_accumulatedImage.width / 8,  // 1024/8 = 128
                                         m_accumulatedImage.height / 8, // 512/8 = 64
                                         m_superSamplingFactor);        // 8
            }

            m_sampleCount++;
            RayTraceKernels.Dispatch(m_initCameraRaysKernel,
                                      m_accumulatedImage.width / 8,
                                      m_accumulatedImage.height / 8,
                                      m_superSamplingFactor);


            float t = Time.time;
            RayTraceKernels.SetVector("_Time", new Vector4(t / 20, t, t * 2, t * 3));
            RayTraceKernels.SetInt("_MaxBounces", MaxBounces);


            RayTraceKernels.Dispatch(m_rayTraceKernel,
                                     m_accumulatedImage.width / 8,
                                     m_accumulatedImage.height / 8,
                                     m_superSamplingFactor * m_bouncesPerPixel);
        }

        private void Update()
        {
            // Resolve the final color directly from the ray accumColor.
            FullScreenResolve.SetBuffer("_Rays", m_raysBuffer);

            // Blit the rays into the accumulated image.
            // This isn't necessary, though it implicitly applies a box filter to the accumulated color,
            // which reduces aliasing artifacts when the viewport size doesn't match the underlying texture
            // size (should only be a problem in-editor).
            FullScreenResolve.SetVector("_AccumulatedImageSize",
                                        new Vector2(m_accumulatedImage.width, m_accumulatedImage.height));
        }

        void ReclaimResources()
        {
            if (m_accumulatedImage != null)
            {
                RenderTexture.ReleaseTemporary(m_accumulatedImage);
                m_accumulatedImage = null;
            }

            m_spheresBuffer?.Release();
            m_spheresBuffer = null;

            m_raysBuffer?.Release();
            m_raysBuffer = null;

            m_fibSamples?.Release();
            m_fibSamples = null;

            FullScreenResolve.SetBuffer("_Rays", m_raysBuffer);
            FullScreenResolve.SetVector("_AccumulatedImageSize", Vector2.zero);
        }

        /// <summary>
        /// Spherical Fibonacci
        /// </summary>
        private void SphericalFib(ref Vector3[] output)
        {
            double n = output.Length / 2;
            double pi = Mathf.PI;
            double dphi = pi * (3 - System.Math.Sqrt(5));
            double phi = 0;
            double dz = 1 / n;
            double z = 1 - dz / 2.0f;
            int[] indices = new int[output.Length];

            for (int j = 0; j < n; j++)
            {
                double zj = z;
                double thetaj = System.Math.Acos(zj);
                double phij = phi % (2 * pi);
                z = z - dz;
                phi = phi + dphi;

                // spherical -> cartesian, with r = 1
                output[j] = new Vector3((float)(System.Math.Cos(phij) * System.Math.Sin(thetaj)),
                                        (float)(zj),
                                        (float)(System.Math.Sin(thetaj) * System.Math.Sin(phij)));
                indices[j] = j;
            }

            if (SphericalFibDebugMesh == null)
            {
                return;
            }

            // The code above only covers a hemisphere, this mirrors it into a sphere.
            for (int i = 0; i < n; i++)
            {
                var vz = output[i];
                vz.y *= -1;
                output[output.Length - i - 1] = vz;
                indices[i + output.Length / 2] = i + output.Length / 2;
            }

            var m = new Mesh();
            m.vertices = output;
            m.SetIndices(indices, MeshTopology.Points, 0);
            SphericalFibDebugMesh.mesh = m;
        }



        public void NotifySceneChanged()
        {
            // Setup the scene.
            var objects = GameObject.FindObjectsOfType<RayTracedSphere>();

            bool reallocate = false;
            if (m_sphereData == null || m_sphereData.Length != objects.Length)
            {
                m_sphereData = new Sphere[objects.Length];
                reallocate = true;
            }

            for (int i = 0; i < objects.Length; i++)
            {
                var obj = objects[i];
                m_sphereData[i] = obj.GetData();
            }

            if (reallocate)
            {
                // Setup GPU memory for the scene.
                const int kFloatsPerSphere = 8;
                if (m_spheresBuffer != null)
                {
                    m_spheresBuffer.Dispose();
                    m_spheresBuffer = null;
                }

                if (m_sphereData.Length > 0)
                {
                    m_spheresBuffer = new ComputeBuffer(m_sphereData.Length, sizeof(float) * kFloatsPerSphere);
                    RayTraceKernels.SetBuffer(m_rayTraceKernel, "_Spheres", m_spheresBuffer);
                }
            }

            if (m_spheresBuffer != null)
            {
                m_spheresBuffer.SetData(m_sphereData);
            }

            m_sceneChanged = true;
        }

    }

}

