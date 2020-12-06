﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;




abstract public class ObjectMaterial
{
    /// <summary>
    /// Return true if the vector is close to zero in all dimensions.
    /// </summary>
    public bool IsNearZero(Vector3 v)
    {
        float s = 0.0001f;
        return (Mathf.Abs(v.x) < s) && (Mathf.Abs(v.y) < s) && (Mathf.Abs(v.z) < s);
    }


    public Vector3 Reflect(Vector3 v, Vector3 n)
    {
        return v - (2 * Vector3.Dot(v, n) * n);
    }

    public Vector3 Refract(Vector3 uv, Vector3 n, float etai_over_etat)
    {
        float cos_theta = Mathf.Min(Vector3.Dot(-uv, n), 1f);
        Vector3 r_out_perp = etai_over_etat * (uv + cos_theta * n);
        Vector3 r_out_parallel = -Mathf.Sqrt(Mathf.Abs(1f - r_out_perp.sqrMagnitude)) * n;
        return r_out_perp + r_out_parallel;
    }


    abstract public bool Scatter(Ray inRay, HitRecord hitRecord, out Color attenuation, out Ray scatteredrRay);

}



public class LambertainMaterial : ObjectMaterial
{
    public Color albedo;


    override public bool Scatter(Ray inRay, HitRecord hitRecord, out Color attenuation, out Ray scatteredrRay)
    {
        var scatterDirection = hitRecord.normal + Random.onUnitSphere;

        // Catch degenerate scatter direction
        if (IsNearZero(scatterDirection))
        {
            scatterDirection = hitRecord.normal;
        }

        scatteredrRay = new Ray(hitRecord.p, scatterDirection);
        attenuation = albedo;
        return true;
    }

    public LambertainMaterial(Color albedo)
    {
        this.albedo = albedo;
    }

}



public class MetalMaterial : ObjectMaterial
{
    public Color albedo;


    public override bool Scatter(Ray inRay, HitRecord hitRecord, out Color attenuation, out Ray scatteredrRay)
    {
        var reflected = Reflect(inRay.direction.normalized, hitRecord.normal);
        scatteredrRay = new Ray(hitRecord.p, reflected);
        attenuation = albedo;
        return (Vector3.Dot(scatteredrRay.direction, hitRecord.normal) > 0f);
    }


    public MetalMaterial(Color albedo)
    {
        this.albedo = albedo;
    }

}

public class FuzzyMetalMaterial : MetalMaterial
{
    public float fuzz;


    public override bool Scatter(Ray inRay, HitRecord hitRecord, out Color attenuation, out Ray scatteredrRay)
    {
        var reflected = Reflect(inRay.direction.normalized, hitRecord.normal);
        scatteredrRay = new Ray(hitRecord.p, reflected + Random.insideUnitSphere * fuzz);
        attenuation = albedo;
        return (Vector3.Dot(scatteredrRay.direction, hitRecord.normal) > 0f);
    }


    public FuzzyMetalMaterial(Color albedo, float fuzz = 0.7f) : base(albedo)
    {
        this.fuzz = (fuzz < 1f) ? fuzz : 1f;
    }

}


public class DielectricMaterial : ObjectMaterial
{
    /// <summary>
    /// Refractive index
    /// </summary>
    public float indexOfRefraction;


    public override bool Scatter(Ray inRay, HitRecord hitRecord, out Color attenuation, out Ray scatteredrRay)
    {
        attenuation = Color.white;

        float refraction_ratio = hitRecord.frontFace ? (1f / indexOfRefraction) : indexOfRefraction;
        Vector3 unit_direction = inRay.direction.normalized;

        float cos_theta = Mathf.Min(Vector3.Dot(-unit_direction, hitRecord.normal), 1f);
        float sin_theta = Mathf.Sqrt(1f - cos_theta * cos_theta);
        bool cannot_refract = refraction_ratio * sin_theta > 1f;

        //
        Vector3 direction;
        if (cannot_refract || Reflectance(cos_theta, refraction_ratio) > Random.value)
        {
            direction = Reflect(unit_direction, hitRecord.normal);
        }
        else
        {
            direction = Refract(unit_direction, hitRecord.normal, refraction_ratio);
        }

        scatteredrRay = new Ray(hitRecord.p, direction);

        return true;
    }


    /// <summary>
    /// Schlick's approximation for reflectance.
    /// </summary>
    float Reflectance(float cosine, float ref_idx)
    {
        float r0 = (1f - ref_idx) / (1f + ref_idx);
        r0 = r0 * r0;
        return r0 + (1f - r0) * Mathf.Pow((1f - cosine), 5f);
    }


    public DielectricMaterial(float indexOfRefraction = 1.5f)
    {
        this.indexOfRefraction = indexOfRefraction;
    }

}
