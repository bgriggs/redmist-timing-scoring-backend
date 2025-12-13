-- PostgreSQL Migration to create OrganizationExtView
-- This should be run as a separate migration after the initial schema is created

-- Drop the view if it exists
DROP VIEW IF EXISTS public."OrganizationExtView";

-- Create the view
CREATE OR REPLACE VIEW public."OrganizationExtView" AS
SELECT 
    o."Id",
    o."ClientId",
    o."ControlLogParams",
    o."ControlLogType",
    COALESCE(o."Logo", d."ImageData") AS "Logo",
    o."Name",
    o."Orbits",
    o."ShortName",
    o."Website",
    o."X2",
    o."MultiloopIp",
    o."MultiloopPort",
    o."OrbitsLogsPath",
    o."RMonitorIp",
    o."RMonitorPort"
FROM public."Organizations" o
CROSS JOIN (
    SELECT "ImageData" 
    FROM public."DefaultOrgImages" 
    LIMIT 1
) d;

COMMENT ON VIEW public."OrganizationExtView" IS 'Organization view with default logo fallback from DefaultOrgImages';
